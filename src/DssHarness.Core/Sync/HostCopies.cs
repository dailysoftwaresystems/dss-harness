using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Text;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Sync;

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
public static class HostCopies
{
    /// <summary>What a worktree's copy adds to the host's repositoryPath, before the worktree's name.</summary>
    public const string WorktreeSuffix = ".worktree-";

    /// <summary>
    /// Whether <paramref name="name"/> is spelt as the name a tree's copies are kept under (<see cref="NameOf"/>):
    /// lower-case letters and digits, joined by hyphens. What follows <see cref="WorktreeSuffix"/> spelt any other way
    /// is no worktree's copy.
    /// </summary>
    /// <param name="name">What follows the suffix in a directory's name.</param>
    public static bool IsCopyName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return CopyName.IsMatch(name);
    }

    /// <summary>
    /// Where <paramref name="host"/> keeps the copy of <paramref name="treeRoot"/>, a tree of <paramref name="context"/>'s
    /// repository, beside where it keeps the main checkout's for this machine (<see cref="RepositoryOf"/>).
    /// </summary>
    /// <param name="context">The repository, its configuration and what the hosts' items declare.</param>
    /// <param name="host">The host.</param>
    /// <param name="treeRoot">The tree: the main checkout, or one of its worktrees.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <exception cref="HarnessException">Where the host keeps its copies is not known: see <see cref="RepositoryOf"/>.</exception>
    public static string Of(HarnessContext context, HostId host, string treeRoot, StringComparison comparison)
        => HostCopyPlace.Of(context, host, treeRoot, comparison).Copy;

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
    /// <param name="root">What the copies are kept beside: the host's main copy.</param>
    /// <param name="family">The family's suffix: <see cref="WorktreeSuffix"/>.</param>
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
        var name = NotANameCharacter.Replace(leaf.ToLowerInvariant(), "-").Trim('-');

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

    /// <summary>
    /// The host's copy of the main checkout that <paramref name="copy"/>, a copy a host keeps, is kept beside: for a
    /// worktree's copy (<c>repo.worktree-o1--xa</c>), the main checkout's copy beside it (<c>repo</c>); for any other, the
    /// copy itself.
    /// </summary>
    /// <param name="copy">The copy, as this machine spells it.</param>
    public static string MainCopyOf(string copy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(copy);

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(copy));
        var at = name.LastIndexOf(WorktreeSuffix, StringComparison.Ordinal);

        return at > 0 && IsCopyName(name[(at + WorktreeSuffix.Length)..]) && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(copy)) is { } parent
            ? Path.Combine(parent, name[..at])
            : copy;
    }

    /// <summary>
    /// The host's copy of the main checkout that <paramref name="tree"/> - a tree a host was sent legs to run in: its copy
    /// of a tree, or a leg's own copy of one kept in that copy's .legs (<see cref="HolderOf"/>) - belongs to
    /// (<see cref="MainCopyOf"/>).
    /// </summary>
    /// <param name="tree">The tree, as the host spells it.</param>
    /// <param name="comparison">How the machine the tree is on compares paths.</param>
    public static string MainCopyOfTree(string tree, StringComparison comparison)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);

        return MainCopyOf(HolderOf(tree, comparison) ?? Path.TrimEndingDirectorySeparator(tree));
    }

    /// <summary>
    /// The tree whose <c>.legs</c> keeps <paramref name="tree"/> - a leg's own copy of it, <c>&lt;tree&gt;/.legs/&lt;key&gt;</c> -
    /// or <see langword="null"/> where no tree's .legs keeps it: the one rule by which a copy made for a leg is told from
    /// the tree it was made of, wherever a machine asks which repository a tree is of.
    /// </summary>
    /// <param name="tree">The tree, as the machine it is on spells it.</param>
    /// <param name="comparison">How that machine compares paths.</param>
    public static string? HolderOf(string tree, StringComparison comparison)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);

        return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(tree)) is { } slot
            && string.Equals(Path.GetFileName(slot), HarnessLayout.LegsDirectoryName, comparison)
            && Path.GetDirectoryName(slot) is { Length: > 0 } holder
                ? holder
                : null;
    }

    /// <summary>Where a host keeps the main checkout's copy for this machine: see <see cref="RepositoryOf"/>.</summary>
    /// <param name="context">The repository, its configuration and what the hosts' items declare.</param>
    /// <param name="host">The host.</param>
    /// <exception cref="HarnessException">Where the host keeps its copies is not known: see <see cref="RepositoryOf"/>.</exception>
    public static string RepositoryPathOf(HarnessContext context, HostId host) => RepositoryOf(context, host).Path;

    /// <summary>
    /// Where a host keeps the main checkout's copy for this machine, and what says so: the <c>REPOSITORY_PATH</c> its item's
    /// .env sets, which is this machine's own; else the repositoryPath config.json declares, the default a fresh clone uses.
    /// The one answer every reader of a host's copies asks - a sync, a leg placed there, host-exec, a listing, a clean - so
    /// none of them reads config.json's past an item that sets its own.
    /// </summary>
    /// <remarks>
    /// Asked on the machine that reaches the host, which holds its item: a host's own copy holds none, and a DssHarness
    /// there finds its copies from the copy it was sent to run in (<see cref="MainCopyOfTree"/>), never from config.json.
    /// </remarks>
    /// <param name="context">The repository, its configuration and what the hosts' items declare.</param>
    /// <param name="host">The host.</param>
    /// <exception cref="HarnessException">
    /// The host's item this machine holds cannot be read (<see cref="HarnessExit.HostUnavailable"/>), so where it keeps its
    /// copies for this machine is not known; or neither the item nor config.json says (<see cref="HarnessExit.ConfigInvalid"/>).
    /// </exception>
    public static HostRepositoryPath RepositoryOf(HarnessContext context, HostId host)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(host);

        var item = context.HostItems.Of(host);

        // Never config.json's in its place: an item set by its user to keep the copy elsewhere would see a sync write, and
        // a clean or a removal delete, where that user keeps something else.
        if (item.Problem is { } problem)
        {
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"Where host {host} keeps its copies for this machine is not known, since its item cannot be read: {problem}.");
        }

        if (item.RepositoryPath is { } set)
        {
            return new HostRepositoryPath(set, $"{Secrets.HostSecretsStore.RepositoryPathKey} in '{item.File}'");
        }

        return DeclaredFor(context.Config, host)?.RepositoryPath is { } declared
            ? new HostRepositoryPath(declared, $"repositoryPath in '{HarnessLayout.ConfigFileRelative}'")
            : throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"Host {host} declares no repositoryPath, so there is nowhere to keep its copy.");
    }

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

    /// <summary>A run of what no copy's name holds.</summary>
    private static readonly Regex NotANameCharacter = LinearRegex.Compile("[^a-z0-9]+");

    /// <summary>A copy's name: words of lower-case letters and digits, joined by hyphens.</summary>
    private static readonly Regex CopyName = LinearRegex.Compile(@"^[a-z0-9]+(-+[a-z0-9]+)*\z");
}

/// <summary>Where a host keeps the main checkout's copy for this machine, and what says so.</summary>
/// <param name="Path">The path, as it is written: <c>~/src/app</c> keeps the account out of it.</param>
/// <param name="From">
/// What set it, as a reader goes to change it: <c>REPOSITORY_PATH in '.harness-config/wslDistros/ubuntu/.env'</c>, or
/// <c>repositoryPath in '.harness-config/config.json'</c>.
/// </param>
public sealed record HostRepositoryPath(string Path, string From);

/// <summary>Where a host keeps the copy of one tree for this machine, and where it keeps the main checkout's beside it.</summary>
/// <param name="Copy">The tree's copy, as it is written.</param>
/// <param name="Repository">Where the main checkout's is, and what set it.</param>
public sealed record HostCopyPlace(string Copy, HostRepositoryPath Repository)
{
    /// <summary>
    /// Where <paramref name="host"/> keeps the copy of <paramref name="treeRoot"/>, a tree of <paramref name="context"/>'s
    /// repository, for this machine (<see cref="HostCopies.RepositoryOf"/>).
    /// </summary>
    /// <param name="context">The repository, its configuration and what the hosts' items declare.</param>
    /// <param name="host">A WSL distribution or an ssh host.</param>
    /// <param name="treeRoot">The tree.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <exception cref="HarnessException">Where the host keeps its copies is not known.</exception>
    public static HostCopyPlace Of(HarnessContext context, HostId host, string treeRoot, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(context);

        var repository = HostCopies.RepositoryOf(context, host);

        return new HostCopyPlace(
            HostCopies.For(repository.Path, context.Layout, treeRoot, context.Layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root), comparison),
            repository);
    }

    /// <summary>
    /// The place as a line says it: <c>keeps this tree's copy at '~/src/app', as REPOSITORY_PATH in '...' sets</c>, with
    /// the main checkout's beside a worktree's.
    /// </summary>
    public string Describe()
        => $"keeps this tree's copy at '{Copy}'"
            + (string.Equals(Copy, Repository.Path, StringComparison.Ordinal) ? string.Empty : $", beside '{Repository.Path}'")
            + $", as {Repository.From} sets";
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

    /// <summary>
    /// The copy of the main checkout this entry's copy was kept beside - its path, as the host spells it, with
    /// <see cref="HostCopies.WorktreeSuffix"/> and its name taken off - or <see langword="null"/> where its path is not where
    /// a worktree's copy is kept. The one place its host is asked about it, by a listing and by a clean's measuring alike, as
    /// its removal acts on its path whatever the host's item or config.json say of where the host keeps its copies now: a
    /// copy recorded beside an earlier repositoryPath is still there, and still this machine's.
    /// </summary>
    /// <remarks>A method, never a property: the record is written as its four fields, and a property would be written too.</remarks>
    public string? KeptBeside()
    {
        var path = Path.TrimEnd('/', '\\');
        var family = HostCopies.WorktreeSuffix + Worktree;

        return path.Length > family.Length && path.EndsWith(family, StringComparison.Ordinal) ? path[..^family.Length] : null;
    }

    /// <summary>
    /// Whether <paramref name="copy"/>, a copy a host listed beside where it keeps the main checkout's copy, is this entry's:
    /// one directory, spelt as the host spells it.
    /// </summary>
    /// <param name="copy">The copy the host listed.</param>
    public bool IsListedAs(HostCopyFound copy)
    {
        ArgumentNullException.ThrowIfNull(copy);

        return string.Equals(Path.TrimEnd('/', '\\'), copy.Path.TrimEnd('/', '\\'), StringComparison.Ordinal);
    }
}

/// <summary>Whether a tree this machine's record names is on this machine.</summary>
public enum TreePresence
{
    /// <summary>The tree is there.</summary>
    There,

    /// <summary>The tree is seen to be gone: the directory it was in can be looked in, and holds nothing where it was.</summary>
    Gone,

    /// <summary>Whether it is there or gone cannot be told, which is never read as gone.</summary>
    Untold,
}

/// <summary>What looking for a tree on this machine found, and, where it could not tell, why.</summary>
/// <param name="Presence">Whether it is there, gone, or neither told.</param>
/// <param name="Why">Why it could not be told, as a line says it after "cannot be told: "; empty where it was told.</param>
public sealed record TreeLook(TreePresence Presence, string Why)
{
    /// <summary>The tree is there.</summary>
    public static TreeLook There { get; } = new(TreePresence.There, string.Empty);

    /// <summary>The tree is seen to be gone.</summary>
    public static TreeLook Gone { get; } = new(TreePresence.Gone, string.Empty);

    /// <summary>Whether the tree is there cannot be told, for <paramref name="why"/>.</summary>
    /// <param name="why">Why, as a line says it after "cannot be told: ".</param>
    public static TreeLook Untold(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        return new TreeLook(TreePresence.Untold, why);
    }

    /// <summary>
    /// Whether it could not be told because the record spells the tree as another system does - written by a sync run
    /// there, which recorded the copy last - so the tree it names may be the very tree asking, as that system spells it:
    /// one checkout used from Windows and from WSL. False for a tree spelt for this machine, absent or unreadable here.
    /// </summary>
    public bool SpeltForAnotherSystem { get; init; }
}

/// <summary>Another tree's entry that holds a copy a sync would claim, and what looking for that tree found.</summary>
/// <param name="Tree">The tree holding it, as the record names it.</param>
/// <param name="Look">Whether that tree is there, or cannot be told to be gone.</param>
public sealed record RecordedHolder(string Tree, TreeLook Look);

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
/// tree before a sync writes into it, and refused while another worktree holds it that is not seen to be gone; and
/// deleting a worktree removes the copies of that tree, and of any tree of its name seen to be gone, never those of one
/// that is there or that cannot be looked at (<see cref="Look(string, string)"/>).
/// </para>
/// </remarks>
/// <param name="fileSystem">Reads and writes the record, and looks for the trees it names.</param>
/// <param name="pathComparison">How this machine compares paths, for telling one tree from another.</param>
public sealed class HostCopyRecord(IFileSystem fileSystem, StringComparison pathComparison)
{
    /// <summary>The record's file name, in <see cref="HarnessLayout.HostCopiesDirectory"/>.</summary>
    public const string FileName = "record.json";

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

    /// <summary>Whether the tree <paramref name="entry"/> is recorded of is on this machine (<see cref="Look(string, string)"/>).</summary>
    /// <param name="entry">An entry of the record.</param>
    public TreeLook Look(HostCopyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return Look(entry.Tree, entry.Worktree);
    }

    /// <summary>
    /// Whether <paramref name="tree"/>, a tree whose copies are kept under <paramref name="name"/>, is on this machine: the
    /// one answer every decision of what a copy is - removed, measured, listed, claimed - is made by. Gone only where that is
    /// seen: its path is whole on this machine, nothing is at it, and the directory it was made in is there to look in - its
    /// parent, or, for an orchestrator's agent's tree (a name of two joined by <see cref="WorktreeAddress.CopySeparator"/>),
    /// its parent's parent where its orchestrator's directory, which goes once its agents are gone, is gone too. Anything
    /// else that is no tree there cannot be told - a path spelt for another system, a directory it was in that is not there
    /// (a volume or a share that is not mounted), one this process may not look in, a file at it, a link at it leading
    /// nowhere that can be looked at - and is never read as gone: a copy is removed for a tree that is gone, and a tree that
    /// only cannot be seen may still be there, using it.
    /// </summary>
    /// <param name="tree">A tree an entry names, as the record spells it.</param>
    /// <param name="name">The name its copies are kept under.</param>
    public TreeLook Look(string tree, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!System.IO.Path.IsPathFullyQualified(tree))
        {
            return TreeLook.Untold($"'{tree}' is no whole path on this machine, so it may be another system's spelling of a tree") with { SpeltForAnotherSystem = true };
        }

        var at = System.IO.Path.TrimEndingDirectorySeparator(tree);

        try
        {
            switch (_fileSystem.KindOf(at))
            {
                case PathKind.Directory:
                    return TreeLook.There;

                case PathKind.Link:
                    return _fileSystem.DirectoryExists(at)
                        ? TreeLook.There
                        : TreeLook.Untold($"'{at}' is a link that leads nowhere this machine can look");

                case PathKind.File:
                    return TreeLook.Untold($"a file stands at '{at}', where the tree was");
            }

            // The directories it may have been made in, nearest first: its own parent, then an agent's orchestrator's parent.
            var levels = name.Contains(WorktreeAddress.CopySeparator, StringComparison.Ordinal) ? 2 : 1;
            var holder = at;

            for (var level = 1; level <= levels; level++)
            {
                if (System.IO.Path.GetDirectoryName(holder) is not { Length: > 0 } above)
                {
                    return TreeLook.Untold($"nothing is at '{holder}', and no directory above it can be looked in");
                }

                holder = above;

                var kind = _fileSystem.KindOf(holder);

                if (kind == PathKind.Directory || (kind == PathKind.Link && _fileSystem.DirectoryExists(holder)))
                {
                    return TreeLook.Gone;
                }

                if (kind != PathKind.None)
                {
                    return TreeLook.Untold($"'{holder}', which it was in, is no directory this machine can look in");
                }
            }

            return TreeLook.Untold($"'{holder}', which it was in, is not there either: a volume or a share it was on may not be mounted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TreeLook.Untold($"'{at}' cannot be looked at: {ex.Message.TrimEnd('.')}");
        }
    }

    /// <summary>Whether two entries' trees are one tree.</summary>
    /// <param name="first">One tree.</param>
    /// <param name="second">The other.</param>
    public bool SameTree(string first, string second)
        => string.Equals(Whole(first), Whole(second), _pathComparison);

    /// <summary>
    /// Records <paramref name="entry"/>'s copy as its tree's, before a sync writes into it - unless another worktree
    /// that still exists holds it, or one that cannot be told to be gone (<see cref="Look(string, string)"/>), which is said
    /// instead, and nothing recorded. An entry of a tree that is seen to be gone is taken over.
    /// </summary>
    /// <param name="layout">The repository.</param>
    /// <param name="entry">The copy a sync is about to make, or bring in step.</param>
    /// <returns>The worktree that holds the copy, and what looking for it found; or <see langword="null"/> once it is recorded.</returns>
    /// <exception cref="HarnessException">The record cannot be read or written: refused.</exception>
    public RecordedHolder? Claim(HarnessLayout layout, HostCopyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        RecordedHolder? holder = null;

        Change(layout, entries =>
        {
            holder = HolderAmong(entries, entry);

            if (holder is not null || (entries.Contains(entry) && entries.Count(kept => kept.SameCopy(entry)) == 1))
            {
                return entries;
            }

            return [.. entries.Where(kept => !kept.SameCopy(entry)), entry];
        });

        return holder;
    }

    /// <summary>
    /// The worktree that holds <paramref name="entry"/>'s copy, which <see cref="Claim"/> would refuse it for, or
    /// <see langword="null"/> where it would record it - read, and nothing recorded: what a dry run of a sync asks, so it is
    /// refused where the sync would be.
    /// </summary>
    /// <param name="layout">The repository.</param>
    /// <param name="entry">The copy a sync would make, or bring in step.</param>
    /// <exception cref="HarnessException">The record cannot be read: refused.</exception>
    public RecordedHolder? Holder(HarnessLayout layout, HostCopyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return HolderAmong(All(layout), entry);
    }

    /// <summary>
    /// Of <paramref name="entries"/>, another worktree that holds <paramref name="entry"/>'s copy and is not seen to be gone -
    /// there still, or not to be told - with what looking for it found: the one rule <see cref="Claim"/> records by and
    /// <see cref="Holder"/> reads by.
    /// </summary>
    private RecordedHolder? HolderAmong(IReadOnlyList<HostCopyEntry> entries, HostCopyEntry entry)
    {
        foreach (var kept in entries.Where(kept => kept.SameCopy(entry) && !SameTree(kept.Tree, entry.Tree)))
        {
            if (Look(kept) is { Presence: not TreePresence.Gone } look)
            {
                return new RecordedHolder(kept.Tree, look);
            }
        }

        return null;
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
            MachineWideFile.Window,
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

        return MachineWideFile.Update(System.IO.Path.GetFullPath(path), MachineWideFile.Window, () => Read(path));
    }

    private void Change(HarnessLayout layout, Func<IReadOnlyList<HostCopyEntry>, IReadOnlyList<HostCopyEntry>> change)
    {
        var path = PathOf(layout);

        MachineWideFile.Update(System.IO.Path.GetFullPath(path), MachineWideFile.Window, () =>
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

                    JsonStateFile.Write(_fileSystem, path, new Document(after), Options);
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
