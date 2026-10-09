using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Core.Sync;

/// <summary>
/// Which of a host's copies of the repository a tree is kept in: the main checkout's at the repositoryPath the
/// host declares, and each worktree's in a copy of its own beside it, so that trees do not share a copy, or the
/// lock on it. A worktree made elsewhere under the name of one that has a copy is refused that copy while the
/// other exists: see <see cref="HostCopyRecord.Claim"/>.
/// </summary>
/// <remarks>
/// <para>
/// One copy per host made every worktree whose legs reached a host wait for every other's: they were synced into
/// one directory, under one lock, and each sync replaced the tree the one before it had put there.
/// </para>
/// <para>
/// Beside the main checkout's copy rather than inside it. The agent a sync starts on a host starts in the copy's
/// parent, which has to be there already, and the main copy's parent is; and a copy inside another would be taken
/// for part of it - by git there, and so by the harness there, which finds its tree through git.
/// </para>
/// </remarks>
public static partial class HostCopies
{
    /// <summary>What a worktree's copy adds to the host's repositoryPath, before the worktree's name.</summary>
    public const string WorktreeSuffix = ".worktree-";

    /// <summary>
    /// What a mutation worker's copy adds to the tree it is a copy of, before the worker's name: a family of copies of
    /// its own, beside the tree. No worktree's copy is named into it, since a copy's name holds no dot
    /// (<see cref="IsCopyName"/>); and a worker kept beside a worktree's copy, whose name goes on from that copy's, is
    /// spelt as no copy's name and so never listed as a worktree's copy.
    /// </summary>
    public const string MutationSuffix = ".mutation-";

    /// <summary>
    /// Whether <paramref name="name"/> is spelt as the name a tree's copies are kept under (<see cref="NameOf"/>):
    /// lower-case letters and digits, joined by hyphens. What follows <see cref="WorktreeSuffix"/> spelt any other way
    /// is no worktree's copy - a mutation worker kept beside one, above all, whose name goes on with
    /// <see cref="MutationSuffix"/>.
    /// </summary>
    /// <param name="name">What follows the suffix in a directory's name.</param>
    public static bool IsCopyName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return CopyName().IsMatch(name);
    }

    /// <summary>
    /// Where <paramref name="host"/> keeps the copy of <paramref name="treeRoot"/>, by the repositoryPath
    /// <paramref name="config"/> declares for it.
    /// </summary>
    /// <param name="config">The configuration that declares the host.</param>
    /// <param name="host">The host.</param>
    /// <param name="layout">The repository the tree belongs to.</param>
    /// <param name="treeRoot">The tree: the main checkout, or one of its worktrees.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <param name="worktreesDirectory">The worktrees root; the configured one, where none is given.</param>
    /// <exception cref="HarnessException">The host declares no repositoryPath.</exception>
    public static string Of(
        HarnessConfig config,
        HostId host,
        HarnessLayout layout,
        string treeRoot,
        StringComparison comparison,
        string? worktreesDirectory = null)
        => For(
            RepositoryPathOf(config, host),
            layout,
            treeRoot,
            worktreesDirectory ?? layout.WorktreesDirectoryUnder(config.Worktrees.Root),
            comparison);

    /// <summary>
    /// Where a host whose main copy is at <paramref name="repositoryPath"/> keeps the copy of
    /// <paramref name="treeRoot"/>: that path for the main checkout, and <see cref="ForWorktree"/> for any other tree.
    /// </summary>
    /// <param name="repositoryPath">Where the host keeps the main checkout's copy.</param>
    /// <param name="layout">The repository the tree belongs to.</param>
    /// <param name="treeRoot">The tree: the main checkout, or one of its worktrees.</param>
    /// <param name="worktreesDirectory">The worktrees root.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    public static string For(string repositoryPath, HarnessLayout layout, string treeRoot, string worktreesDirectory, StringComparison comparison)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);

        return string.Equals(Whole(treeRoot), Whole(layout.MainCheckoutRoot), comparison)
            ? repositoryPath
            : ForWorktree(repositoryPath, NameOf(worktreesDirectory, treeRoot, comparison));
    }

    /// <summary>The copy a worktree kept under <paramref name="name"/> has beside the main copy at <paramref name="repositoryPath"/>.</summary>
    /// <param name="repositoryPath">Where the host keeps the main checkout's copy.</param>
    /// <param name="name">The name the worktree's copies are kept under: see <see cref="NameOf"/>.</param>
    public static string ForWorktree(string repositoryPath, string name) => InFamily(repositoryPath, WorktreeSuffix, name);

    /// <summary>
    /// The copy of the <paramref name="family"/> kept under <paramref name="name"/> beside <paramref name="root"/>:
    /// <c>&lt;root&gt;&lt;family&gt;&lt;name&gt;</c>.
    /// </summary>
    /// <param name="root">What the copies are kept beside: the host's main copy, or the tree a worker copies.</param>
    /// <param name="family">The family's suffix: <see cref="WorktreeSuffix"/> or <see cref="MutationSuffix"/>.</param>
    /// <param name="name">The name the copy is kept under.</param>
    public static string InFamily(string root, string family, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return root.TrimEnd('/', '\\') + family + name;
    }

    /// <summary>
    /// The name a tree's copies are kept under. A worktree under the root keeps its address's
    /// (<see cref="WorktreeAddress.CopyName"/>): its own name, or its orchestrator's and its own joined by two hyphens, so
    /// two orchestrators' agents of one name keep copies apart. Any other tree keeps its directory's, spelt as a
    /// worktree's name is - lower-case letters and digits, and a hyphen for each run of anything else.
    /// </summary>
    /// <param name="worktreesDirectory">The worktrees root, as configured.</param>
    /// <param name="treeRoot">The tree, as written or with its links resolved, as git names one.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <remarks>
    /// A root reached through a link - itself one, or a directory above it - is tried where it leads too, with the tree
    /// where it leads: git names a tree by where it resolved, and a leg by the path it was configured with, and the two
    /// must name one copy alike.
    /// </remarks>
    public static string NameOf(string worktreesDirectory, string treeRoot, StringComparison comparison)
    {
        if (AddressOf(worktreesDirectory, treeRoot, comparison) is { } address)
        {
            return address.CopyName;
        }

        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(treeRoot));
        var name = NotANameCharacter().Replace(leaf.ToLowerInvariant(), "-").Trim('-');

        return name.Length > 0 ? name : "worktree";
    }

    /// <summary>
    /// The address of <paramref name="treeRoot"/> under <paramref name="worktreesDirectory"/>, or <see langword="null"/>
    /// where it is no worktree there.
    /// </summary>
    /// <param name="worktreesDirectory">The worktrees root, as configured.</param>
    /// <param name="treeRoot">The tree, as written or with its links resolved, as git names one.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <remarks>Tried, as <see cref="NameOf"/> is, as written and with every link along either followed.</remarks>
    public static WorktreeAddress? AddressOf(string worktreesDirectory, string treeRoot, StringComparison comparison)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreesDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);

        return WorktreeAddress.OfTree(worktreesDirectory, treeRoot, comparison) ?? Resolved(worktreesDirectory, treeRoot, comparison);
    }

    /// <summary>The address of <paramref name="treeRoot"/> under <paramref name="worktreesDirectory"/> with every link along either followed; null where there is none, or a link cannot be read.</summary>
    private static WorktreeAddress? Resolved(string worktreesDirectory, string treeRoot, StringComparison comparison)
    {
        try
        {
            var root = LinkPaths.Resolve(worktreesDirectory);
            return WorktreeAddress.OfTree(root, treeRoot, comparison) ?? WorktreeAddress.OfTree(root, LinkPaths.Resolve(treeRoot), comparison);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The host whose copy of the repository this machine's tree is, where it runs legs another machine sent it as a WSL
    /// distribution or an ssh host; <see langword="null"/> where its tree is the repository itself - a command typed here,
    /// or one told it runs here as this machine's own, <c>local</c>, which keeps no copy.
    /// </summary>
    /// <param name="here">The host this machine is to the machine that sent the legs here, or <see langword="null"/>.</param>
    public static HostId? KeptAs(HostId? here) => here is { Kind: not HostKind.Local } ? here : null;

    /// <summary>Where a host keeps the main checkout's copy, as the configuration declares it.</summary>
    /// <param name="config">The configuration that declares the host.</param>
    /// <param name="host">The host.</param>
    /// <exception cref="HarnessException">The host declares no repositoryPath.</exception>
    public static string RepositoryPathOf(HarnessConfig config, HostId host)
        => DeclaredFor(config, host)?.RepositoryPath ?? throw new HarnessException(
            HarnessExit.ConfigInvalid,
            $"Host {host} declares no repositoryPath, so there is nowhere to keep its copy.");

    /// <summary>What <paramref name="config"/> declares for <paramref name="host"/>, or <see langword="null"/> where it declares nothing.</summary>
    /// <param name="config">The configuration.</param>
    /// <param name="host">A WSL distribution or an ssh host.</param>
    public static RemoteHostConfig? DeclaredFor(HarnessConfig config, HostId host)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(host);

        return host.Kind switch
        {
            HostKind.Wsl => config.Hosts.Wsl.GetValueOrDefault(host.Name),
            HostKind.Ssh => config.Hosts.Ssh.GetValueOrDefault(host.Name),
            _ => null,
        };
    }

    private static string Whole(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotANameCharacter();

    [GeneratedRegex(@"^[a-z0-9]+(-+[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex CopyName();
}

/// <summary>One worktree's copy on one host.</summary>
/// <param name="Worktree">The name the worktree's copies are kept under.</param>
/// <param name="Host">The host, as <see cref="HostId.ToString"/> spells it.</param>
/// <param name="Path">Where the host keeps the copy.</param>
/// <param name="Tree">The worktree on this machine the copy is of, as a full path.</param>
public sealed record HostCopyEntry(string Worktree, string Host, string Path, string Tree)
{
    /// <summary>Whether <paramref name="other"/> records the same directory on the same host.</summary>
    /// <param name="other">Another entry.</param>
    public bool SameCopy(HostCopyEntry other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path, other.Path, StringComparison.Ordinal);
    }
}

/// <summary>
/// Which hosts hold a copy of which worktree, and of which tree on this machine, so that deleting a worktree removes
/// its copies from the hosts that hold one and asks no other - a host that is switched off is waited for only where
/// it holds something to remove - and so that no two worktrees are ever synced into one copy.
/// </summary>
/// <remarks>
/// <para>
/// Kept in <see cref="HarnessLayout.HostCopiesDirectory"/>, in the main checkout, which ignores itself.
/// </para>
/// <para>
/// A copy is named for its worktree's directory, and a worktree made by hand, or by another tool, outside the
/// worktrees root can be named as one inside it is. So each entry names the tree it is of: a copy is claimed for a
/// tree before a sync writes into it, and refused while another worktree that still exists holds it; and deleting a
/// worktree removes the copies of that tree, and of any tree of its name that no longer exists, never those of one
/// that does.
/// </para>
/// </remarks>
/// <param name="fileSystem">Reads and writes the record, and tells a tree that exists from one that is gone.</param>
/// <param name="pathComparison">How this machine compares paths, for telling one tree from another.</param>
public sealed class HostCopyRecord(IFileSystem fileSystem, StringComparison pathComparison)
{
    /// <summary>The record's file name, in <see cref="HarnessLayout.HostCopiesDirectory"/>.</summary>
    public const string FileName = "record.json";

    /// <summary>How long a change waits for another process changing the record.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Options = JsonStateFile.Options;

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly StringComparison _pathComparison = pathComparison;

    /// <summary>Where <paramref name="layout"/>'s record is kept.</summary>
    /// <param name="layout">The repository.</param>
    public static string PathOf(HarnessLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return System.IO.Path.Combine(layout.HostCopiesDirectory, FileName);
    }

    /// <summary>Whether <paramref name="tree"/> still exists on this machine.</summary>
    /// <param name="tree">A tree an entry names.</param>
    public bool Exists(string tree) => _fileSystem.DirectoryExists(tree);

    /// <summary>Whether two entries' trees are one tree.</summary>
    /// <param name="first">One tree.</param>
    /// <param name="second">The other.</param>
    public bool SameTree(string first, string second)
        => string.Equals(Whole(first), Whole(second), _pathComparison);

    /// <summary>
    /// Records <paramref name="entry"/>'s copy as its tree's, before a sync writes into it - unless another worktree
    /// that still exists holds it, which is said instead, and nothing recorded. An entry of a tree that is gone is
    /// taken over.
    /// </summary>
    /// <param name="layout">The repository.</param>
    /// <param name="entry">The copy a sync is about to make, or bring in step.</param>
    /// <returns>The tree of the worktree that holds the copy, or <see langword="null"/> once it is recorded.</returns>
    /// <exception cref="HarnessException">The record cannot be read or written: refused.</exception>
    public string? Claim(HarnessLayout layout, HostCopyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string? holder = null;

        Change(layout, entries =>
        {
            holder = entries
                .FirstOrDefault(kept => kept.SameCopy(entry) && !SameTree(kept.Tree, entry.Tree) && Exists(kept.Tree))
                ?.Tree;

            if (holder is not null || (entries.Contains(entry) && entries.Count(kept => kept.SameCopy(entry)) == 1))
            {
                return entries;
            }

            return [.. entries.Where(kept => !kept.SameCopy(entry)), entry];
        });

        return holder;
    }

    /// <summary>Stops recording <paramref name="entry"/>, once its copy is gone or is not the harness's to remove.</summary>
    /// <param name="layout">The repository.</param>
    /// <param name="entry">The copy.</param>
    /// <exception cref="HarnessException">The record cannot be read or written: refused.</exception>
    public void Forget(HarnessLayout layout, HostCopyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Change(layout, entries => [.. entries.Where(kept => kept != entry)]);
    }

    /// <summary>
    /// The entry recorded now for <paramref name="entry"/>'s copy - the same directory on the same host - of whichever
    /// tree, or <see langword="null"/> where none is.
    /// </summary>
    /// <param name="layout">The repository.</param>
    /// <param name="entry">An entry read before.</param>
    /// <exception cref="HarnessException">The record cannot be read: refused.</exception>
    public HostCopyEntry? Holding(HarnessLayout layout, HostCopyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var path = PathOf(layout);

        return MachineWideFile.Update(
            System.IO.Path.GetFullPath(path),
            Window,
            () => Read(path).FirstOrDefault(kept => kept.SameCopy(entry)));
    }

    /// <summary>Every copy recorded under the name <paramref name="worktree"/>, of whichever tree.</summary>
    /// <param name="layout">The repository.</param>
    /// <param name="worktree">The name the copies are kept under.</param>
    /// <exception cref="HarnessException">The record cannot be read: refused.</exception>
    public IReadOnlyList<HostCopyEntry> Of(HarnessLayout layout, string worktree)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktree);

        return [.. All(layout).Where(entry => string.Equals(entry.Worktree, worktree, StringComparison.Ordinal))];
    }

    /// <summary>Every copy recorded, of whichever worktree, in the order they were recorded.</summary>
    /// <param name="layout">The repository.</param>
    /// <exception cref="HarnessException">The record cannot be read: refused.</exception>
    public IReadOnlyList<HostCopyEntry> All(HarnessLayout layout)
    {
        var path = PathOf(layout);

        return MachineWideFile.Update(System.IO.Path.GetFullPath(path), Window, () => Read(path));
    }

    private void Change(HarnessLayout layout, Func<IReadOnlyList<HostCopyEntry>, IReadOnlyList<HostCopyEntry>> change)
    {
        var path = PathOf(layout);

        MachineWideFile.Update(System.IO.Path.GetFullPath(path), Window, () =>
        {
            var before = Read(path);
            var after = change(before);

            if (after.SequenceEqual(before))
            {
                return 0;
            }

            MachineWideFile.Written(
                $"The record of which hosts hold a copy of which worktree, '{path}',",
                "Until it can be, no worktree is synced to a host, nor has its copies there removed.",
                () =>
                {
                    _fileSystem.CreateDirectory(layout.HostCopiesDirectory);

                    if (!_fileSystem.FileExists(layout.HostCopiesIgnoreFile))
                    {
                        _fileSystem.WriteAllTextAtomic(layout.HostCopiesIgnoreFile, HarnessLayout.SelfIgnoreRule);
                    }

                    _fileSystem.WriteAllTextAtomic(path, JsonSerializer.Serialize(new Document(after), Options));
                });

            return 0;
        });
    }

    /// <summary>
    /// Every copy the record at <paramref name="path"/> holds. Refused rather than read as empty, as the run lock is, where
    /// it cannot be read: an empty record forgets every copy it held, and a worktree deleted then leaves its copies on the
    /// hosts with nothing left that knows where they are.
    /// </summary>
    /// <exception cref="HarnessException">The record is there and cannot be read.</exception>
    private IReadOnlyList<HostCopyEntry> Read(string path)
        => MachineWideFile.Read<Document>(
            _fileSystem,
            path,
            ex => $"'{path}' records which hosts hold a copy of which worktree, and it cannot be read: {ex.Message.TrimEnd('.')}. "
                + "No worktree is synced to a host, nor has its copies there removed, until it can. Delete it to forget the "
                + "copies it records, each of which then stays on its host for you to remove.")?.Copies
            ?? [];

    private static string Whole(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    /// <summary>The record as it is written.</summary>
    /// <param name="Copies">Every copy recorded.</param>
    private sealed record Document(IReadOnlyList<HostCopyEntry> Copies);
}
