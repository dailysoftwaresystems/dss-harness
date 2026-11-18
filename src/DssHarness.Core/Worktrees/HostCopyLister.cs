using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;

namespace DssHarness.Core.Worktrees;

/// <summary>The copies recorded of one tree on this machine, kept under one name.</summary>
/// <param name="Name">The name its copies are kept under.</param>
/// <param name="Tree">The tree, as a full path.</param>
/// <param name="Copies">Its copies, in the order they were recorded.</param>
public sealed record RecordedTree(string Name, string Tree, IReadOnlyList<HostCopyEntry> Copies)
{
    /// <summary>
    /// For a tree that is gone, where a worktree here now bears the name its copies are kept under: that worktree's
    /// directory. Deleting it would remove these copies too, as the copies of a gone worktree of its name; removing them
    /// alone is <c>clean --gone-copies</c>'s, which tells them by their tree. <see langword="null"/> where no worktree here
    /// bears it.
    /// </summary>
    public string? NameNowAt { get; init; }

    /// <summary>For a tree that cannot be looked at, why (<see cref="TreeLook.Why"/>); empty for any other.</summary>
    public string Why { get; init; } = string.Empty;
}

/// <summary>Where a copy a host keeps stands with this machine's record.</summary>
public enum CopyStanding
{
    /// <summary>Recorded as the copy of a worktree the listing holds.</summary>
    Listed,

    /// <summary>Recorded as the copy of a tree still here, outside the worktrees root.</summary>
    Elsewhere,

    /// <summary>
    /// Recorded as the copy of a tree seen to be gone: <c>clean --gone-copies</c> deals with it, told by that tree, never
    /// by the name it is kept under, which a worktree here may bear now.
    /// </summary>
    Gone,

    /// <summary>
    /// Recorded as the copy of a tree that cannot be looked at (<see cref="HostCopyRecord.Look(string, string)"/>): nothing
    /// here removes or forgets it while that is so, a clean among them.
    /// </summary>
    Untold,

    /// <summary>Not recorded here: made from another checkout or another machine, or forgotten here.</summary>
    Unrecorded,
}

/// <summary>One copy a host keeps, and where it stands with this machine's record.</summary>
/// <param name="Found">What the host said of it.</param>
/// <param name="Standing">Where it stands with the record.</param>
/// <param name="Tree">
/// The tree the record says it is of, where the record holds it; where it does not, the tree its marker says it was
/// last made from, where the marker names one.
/// </param>
public sealed record HostCopySeen(HostCopyFound Found, CopyStanding Standing, string? Tree)
{
    /// <summary>
    /// For a copy the record does not hold whose marker names its tree: whether that tree is a directory on this machine -
    /// a tree here, its copy forgotten here - rather than one gone from this machine, or another machine's.
    /// </summary>
    public bool TreeHere { get; init; }

    /// <summary>
    /// Where its tree - the one the record names, or the one its marker names - cannot be looked at, why; empty where it
    /// was told.
    /// </summary>
    public string Why { get; init; } = string.Empty;
}

/// <summary>A copy this machine records a host keeping that the host does not keep, and where its tree stands.</summary>
/// <param name="Entry">The copy, as the record holds it.</param>
/// <param name="Standing">
/// Where the tree it is recorded of stands - a listed worktree, a tree still here outside the worktrees root, one seen to
/// be gone, or one that cannot be looked at - which decides what forgets the entry.
/// </param>
public sealed record HostCopyMissing(HostCopyEntry Entry, CopyStanding Standing)
{
    /// <summary>Where its tree cannot be looked at, why; empty where it was told.</summary>
    public string Why { get; init; } = string.Empty;
}

/// <summary>
/// What a host keeps beside a copy of the main checkout this machine's record says it kept copies beside before - a
/// repositoryPath the host's item or config.json named then - asked because a recorded copy is there.
/// </summary>
/// <param name="RepositoryPath">The earlier copy of the main checkout, as the record spells it.</param>
public sealed record HostCopiesBeside(string RepositoryPath)
{
    /// <summary>Every copy it keeps there, in the order of their names.</summary>
    public IReadOnlyList<HostCopySeen> Copies { get; init; } = [];

    /// <summary>Why it could not be asked about that place, where it could not: what it keeps there is then unknown.</summary>
    public string? Unasked { get; init; }
}

/// <summary>What one declared host was asked about the worktree copies it keeps, and what it answered.</summary>
/// <param name="Host">The host.</param>
/// <param name="RepositoryPath">
/// Where it keeps the main checkout's copy for this machine, beside which it keeps the rest
/// (<see cref="HostCopies.RepositoryOf"/>); <see langword="null"/> where that is not known - its item cannot be read - and
/// it was asked nothing, for no other path stands in for it.
/// </param>
public sealed record HostCopiesAnswer(HostId Host, string? RepositoryPath)
{
    /// <summary>Every copy it keeps there, in the order of their names.</summary>
    public IReadOnlyList<HostCopySeen> Copies { get; init; } = [];

    /// <summary>
    /// What it keeps beside each earlier place the record says a copy of this machine's was kept beside, in the order the
    /// record first names each: a copy recorded there is still this machine's, and the one its removal acts on.
    /// </summary>
    public IReadOnlyList<HostCopiesBeside> Earlier { get; init; } = [];

    /// <summary>
    /// The copies recorded on it that it does not keep where each is recorded, each with where its tree stands: a gone
    /// tree's is forgotten by <c>clean --gone-copies</c>, and the next sync of a tree still here makes its copy again.
    /// </summary>
    public IReadOnlyList<HostCopyMissing> Missing { get; init; } = [];

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

    /// <summary>
    /// Trees seen to be gone, with the copies they left: <c>clean --gone-copies</c> removes them, told by their tree,
    /// whatever worktree here bears the name they are kept under now.
    /// </summary>
    public IReadOnlyList<RecordedTree> Gone { get; init; } = [];

    /// <summary>
    /// Trees that cannot be looked at, each with why and the copies recorded of it: nothing here removes or forgets them
    /// while that is so, since such a tree may still be there, using them.
    /// </summary>
    public IReadOnlyList<RecordedTree> Untold { get; init; } = [];

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
public interface IHostCopyLister : DssHarness.Core.Application.IUseCase
{
    /// <summary>
    /// The copies this machine records hosts holding - of the worktrees <paramref name="worktrees"/> lists, of trees
    /// still here outside the worktrees root, of trees seen to be gone, and of trees that cannot be looked at - and, where
    /// <paramref name="askHosts"/> is set, what each host the configuration declares keeps beside where it keeps this
    /// machine's copies now, and beside each earlier place the record says a copy was kept, set against that record.
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
/// The record is what deleting a worktree acts on, so a copy it holds of a worktree here is one <c>delete-worktree</c>
/// deals with, under the name it is kept under; one of a tree that is gone is <c>clean --gone-copies</c>'s, told by that
/// tree, since the name may be a worktree's here now, which deleting would take with it. A copy a host keeps that the
/// record does not hold was made from another checkout or another machine, or forgotten here, and nothing here reaches
/// it: asking the hosts is how such a copy is found, with the tree its marker says it was made from, and how a copy the
/// record holds is found gone. Nothing here changes a host or the record, so no lock is taken.
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
        var untold = new List<RecordedTree>();

        foreach (var tree in Trees(recorded))
        {
            if (ListedAt(worktrees, tree.Tree) is { } listed)
            {
                ofListed[listed.Name] = [.. ofListed.GetValueOrDefault(listed.Name) ?? [], .. tree.Copies];
                continue;
            }

            // The one answer a clean removes by: a tree that cannot be looked at is never listed as gone.
            switch (_record.Look(tree.Tree, tree.Name))
            {
                case { Presence: TreePresence.There }:
                    elsewhere.Add(tree);
                    break;

                case { Presence: TreePresence.Gone }:
                    gone.Add(tree);
                    break;

                case var look:
                    untold.Add(tree with { Why = look.Why });
                    break;
            }
        }

        var listing = new HostCopyListing
        {
            OfListed = ofListed,
            Elsewhere = [.. elsewhere.OrderBy(tree => tree.Name, StringComparer.Ordinal)],

            // A gone tree's name a worktree here bears now is said with it: deleting that worktree would take these too.
            Gone =
            [
                .. gone
                    .Select(tree => tree with { NameNowAt = NamedHere(worktrees, elsewhere, tree.Name) })
                    .OrderBy(tree => tree.Name, StringComparer.Ordinal),
            ],
            Untold = [.. untold.OrderBy(tree => tree.Name, StringComparer.Ordinal)],
        };

        if (!askHosts)
        {
            return listing;
        }

        var declared = Declared(context.Config);
        var answers = new List<HostCopiesAnswer>();

        foreach (var host in declared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            answers.Add(await AskAsync(context, host, recorded, worktrees, cancellationToken).ConfigureAwait(false));
        }

        return listing with
        {
            Hosts = answers,
            Undeclared = [.. recorded.Where(entry => !declared.Any(host => OnHost(entry, host)))],
        };
    }

    /// <summary>
    /// What <paramref name="host"/> keeps beside where it keeps the main checkout's copy for this machine
    /// (<see cref="HostCopies.RepositoryOf"/>), and beside each earlier place a copy recorded on it was kept beside
    /// (<see cref="HostCopyEntry.KeptBeside"/>), set against the record; where the first is not known, why, with no path.
    /// </summary>
    private async Task<HostCopiesAnswer> AskAsync(
        HarnessContext context,
        HostId host,
        IReadOnlyList<HostCopyEntry> recorded,
        IReadOnlyList<WorktreeListing> worktrees,
        CancellationToken cancellationToken)
    {
        string repositoryPath;

        try
        {
            repositoryPath = HostCopies.RepositoryPathOf(context, host);
        }
        catch (HarnessException ex)
        {
            // Never listed, nor named, beside config.json's in its place: an item set to keep the copies elsewhere would
            // have this machine's copies there read as none, whatever else is there as copies, and its reader sent to the
            // path the item was written to replace.
            return new HostCopiesAnswer(host, null) { Unasked = ex.Message, ExitCode = ex.ExitCode };
        }

        var asked = new HostCopiesAnswer(host, repositoryPath);
        var onHost = recorded.Where(entry => OnHost(entry, host)).ToList();
        ISyncTransport transport;
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

            transport = _transports.For(report);
            found = await transport.ListCopiesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return asked with { Unasked = ex.Message, ExitCode = ex.ExitCode };
        }

        // Each copy is looked for beside the copy of the main checkout it was kept beside, as the record has it - the place
        // its removal acts on, and a clean measures - and so a copy recorded beside an earlier repositoryPath is listed there.
        var main = repositoryPath.TrimEnd('/', '\\');
        var earlier = new List<HostCopiesBeside>();
        var exitCode = HarnessExit.Success;
        var everywhere = new List<HostCopyFound>(found);

        foreach (var beside in onHost.Select(entry => entry.KeptBeside()).OfType<string>().Where(beside => beside != main).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var there = await transport.ListCopiesAsync(beside, cancellationToken).ConfigureAwait(false);

                everywhere.AddRange(there);
                earlier.Add(new HostCopiesBeside(beside) { Copies = [.. there.Select(copy => Seen(copy, onHost.FirstOrDefault(entry => entry.IsListedAs(copy)), worktrees))] });
            }
            catch (HarnessException ex)
            {
                earlier.Add(new HostCopiesBeside(beside) { Unasked = ex.Message });
                exitCode = Math.Max(exitCode, ex.ExitCode);
            }
        }

        // A place that could not be asked holds copies nobody knows of: none recorded there is said missing.
        var unasked = earlier.Where(place => place.Unasked is not null).Select(place => place.RepositoryPath).ToHashSet(StringComparer.Ordinal);

        return asked with
        {
            Copies = [.. found.Select(copy => Seen(copy, onHost.FirstOrDefault(entry => entry.IsListedAs(copy)), worktrees))],
            Earlier = earlier,
            Missing =
            [
                .. onHost
                    .Where(entry => !everywhere.Any(entry.IsListedAs) && !(entry.KeptBeside() is { } beside && unasked.Contains(beside)))
                    .Select(Missing),
            ],
            ExitCode = exitCode,
        };

        HostCopyMissing Missing(HostCopyEntry entry)
        {
            var (standing, why) = StandingOf(entry.Tree, entry.Worktree, worktrees);
            return new HostCopyMissing(entry, standing) { Why = why };
        }
    }

    /// <summary>
    /// Where <paramref name="copy"/> stands with the record, which holds it as <paramref name="entry"/> or not at all: one
    /// it does not hold is said with the tree its marker names, and whether that tree is here, or why that cannot be told.
    /// </summary>
    private HostCopySeen Seen(HostCopyFound copy, HostCopyEntry? entry, IReadOnlyList<WorktreeListing> worktrees)
    {
        if (entry is not null)
        {
            var (standing, why) = StandingOf(entry.Tree, entry.Worktree, worktrees);
            return new HostCopySeen(copy, standing, entry.Tree) { Why = why };
        }

        var look = copy.Tree is { Length: > 0 } made ? _record.Look(made, copy.Name) : null;

        return new HostCopySeen(copy, CopyStanding.Unrecorded, copy.Tree)
        {
            TreeHere = look is { Presence: TreePresence.There },
            Why = look is { Presence: TreePresence.Untold } ? look.Why : string.Empty,
        };
    }

    /// <summary>
    /// Where a tree the record names stands - a listed worktree, a tree still here, one seen to be gone, or one that cannot
    /// be looked at, with why - by the one answer a clean removes by.
    /// </summary>
    private (CopyStanding Standing, string Why) StandingOf(string tree, string name, IReadOnlyList<WorktreeListing> worktrees)
    {
        if (ListedAt(worktrees, tree) is not null)
        {
            return (CopyStanding.Listed, string.Empty);
        }

        var look = _record.Look(tree, name);

        return look.Presence switch
        {
            TreePresence.There => (CopyStanding.Elsewhere, string.Empty),
            TreePresence.Gone => (CopyStanding.Gone, string.Empty),
            _ => (CopyStanding.Untold, look.Why),
        };
    }

    /// <summary>
    /// The directory of the worktree here that bears <paramref name="name"/>, the name copies are kept under - a listed
    /// worktree, or a tree still here outside the worktrees root whose copies are recorded under it - or
    /// <see langword="null"/> where none does.
    /// </summary>
    private static string? NamedHere(IReadOnlyList<WorktreeListing> worktrees, IReadOnlyList<RecordedTree> elsewhere, string name)
        => worktrees.FirstOrDefault(worktree => worktree.Path.Length > 0
                && string.Equals(worktree.Name, WorktreeAddress.OfCopyName(name), StringComparison.Ordinal))?.Path
            ?? elsewhere.FirstOrDefault(tree => string.Equals(tree.Name, name, StringComparison.Ordinal))?.Tree;

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
    /// The hosts that can keep copies - each WSL distribution and ssh host the configuration declares - in the order a
    /// reader looks them up.
    /// </summary>
    private static IReadOnlyList<HostId> Declared(HarnessConfig config)
        =>
        [
            .. config.Hosts.Wsl.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(HostId.Wsl),
            .. config.Hosts.Ssh.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(HostId.Ssh),
        ];

    /// <summary>Whether <paramref name="entry"/> records a copy on <paramref name="host"/>, as the record spells a host.</summary>
    private static bool OnHost(HostCopyEntry entry, HostId host)
        => string.Equals(entry.Host, host.ToString(), StringComparison.OrdinalIgnoreCase);
}
