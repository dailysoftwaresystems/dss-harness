using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RepoHarness.Core.Build;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// One family of the mutation workers kept beside a tree: those a sweep of one variant keeps there, or those a self-test
/// of that variant does - each <c>&lt;tree&gt;.mutation-&lt;key&gt;&lt;mark&gt;-&lt;n&gt;</c>, numbered from 1: the key
/// its variant's (<see cref="MutationWorkers.KeyOf"/>), the mark <see cref="OwnMark"/> for the variant's own workers
/// and <see cref="SelfTestMark"/> for its self-test's.
/// </summary>
/// <param name="TreeRoot">The tree the workers are kept beside.</param>
/// <param name="Key">The variant's key: <see cref="KeyLength"/> hexadecimal digits, in lower case.</param>
/// <param name="SelfTest">Whether they are a self-test's, copies of the fixture this tool carries rather than of the tree.</param>
/// <remarks>
/// A key, never the variant's name: a worker is a second tree, built as deep as the first, so on a machine whose paths
/// are bounded every character its name adds is one the tree's own path must leave free. Spelt out, a variant's name
/// stood in a worker's path twice - in its name, and in the build directory within it - and a repository whose path
/// budget is reckoned to the character had room for neither. A worker's name adds the same few characters whatever
/// its variant is called, and which leg a worker is the lines of the sweep that makes it, and of the clean that
/// removes it, say.
/// </remarks>
public sealed record WorkerFamily(string TreeRoot, string Key, bool SelfTest = false)
{
    /// <summary>How many characters a variant's key is.</summary>
    public const int KeyLength = 7;

    /// <summary>What follows the key in the name of a variant's own workers, which its sweep drives its arms in.</summary>
    public const char OwnMark = 'w';

    /// <summary>What follows the key in the name of the workers of a variant's self-test.</summary>
    public const char SelfTestMark = 's';

    /// <summary>The digits a key is written in.</summary>
    private const string KeyDigits = "0123456789abcdef";

    /// <summary>The name its workers are kept under, before each one's number: the variant's key, then the family's mark.</summary>
    public string Name => Key + (SelfTest ? SelfTestMark : OwnMark);

    /// <summary>What every worker of the family is named after: its path, before the hyphen and the worker's number.</summary>
    public string Root => HostCopies.InFamily(TreeRoot, HostCopies.MutationSuffix, Name);

    /// <summary>
    /// The key a sweep of the variant is locked by, its self-test with it: the variant's key with no mark, which no
    /// worker is named, so a sweep and a self-test of one leg never run at once.
    /// </summary>
    public string SweepKey => HostCopies.InFamily(TreeRoot, HostCopies.MutationSuffix, Key);

    /// <summary>Where worker <paramref name="number"/> of the family is kept.</summary>
    /// <param name="number">The worker's number, from 1.</param>
    public string PathOf(int number)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        return Root + "-" + number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The family of <paramref name="treeRoot"/> whose workers are kept under <paramref name="name"/>, or
    /// <see langword="null"/> where it is spelt as no family's: a key and one of the two marks, and nothing else, so a
    /// family's name is spelt one way.
    /// </summary>
    /// <param name="treeRoot">The tree the workers are kept beside.</param>
    /// <param name="name">The name the workers are kept under, before each one's number.</param>
    public static WorkerFamily? Named(string treeRoot, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Length == KeyLength + 1
            && name.AsSpan(0, KeyLength).IndexOfAnyExcept(KeyDigits) < 0
            && name[KeyLength] is OwnMark or SelfTestMark
            ? new WorkerFamily(treeRoot, name[..KeyLength], name[KeyLength] == SelfTestMark)
            : null;
    }
}

/// <summary>
/// Where a leg's mutation workers are kept, and the lock a sweep of the leg takes: each worker a copy beside the leg's
/// tree, named by the key of the leg's variant and the worker's number, in a family of copies of its own.
/// </summary>
/// <remarks>
/// <para>
/// A worker is <c>&lt;tree&gt;.mutation-&lt;key&gt;w-&lt;n&gt;</c>, numbered from 1, its build directory the variant's
/// own within it, as the leg's is within the tree. It stays between sweeps, synced again by content before each, so its
/// build is warm: the sweep only ever reads the tree, and builds and mutates in its workers alone. A self-test's workers
/// are kept the same way, in a family of their own (<see cref="WorkerFamily.SelfTestMark"/>): copies of the fixture
/// this tool carries, beside the tree of the leg whose toolchain builds them.
/// </para>
/// <para>
/// A sweep of a leg variant is locked by <see cref="SweepLock(PlacedLeg, RunId, string)"/>: what its workers are kept under, spelt as one more copy
/// beside the tree would be, which no copy is - so it conflicts with another sweep of the leg, and with a clean of its
/// workers, and never with a build, test or sync of the leg, which a sweep of hours must never hold off.
/// </para>
/// <para>
/// The workers go with their tree: whatever removes a worktree, or a host's copy of one, removes the workers kept beside
/// it first, and leaves the tree where a sweep still running holds one of them.
/// </para>
/// </remarks>
public static class MutationWorkers
{
    /// <summary>The suffix of a worker's claim file, beside the worker's copy: which run's sweep is using it.</summary>
    public const string ClaimSuffix = ".claim.json";

    /// <summary>
    /// How a claim on a worker's copy speaks of it, in the lines of <paramref name="commandName"/>: claimed without making
    /// the copy, which the sync that makes it refuses where a directory it did not make is there already.
    /// </summary>
    /// <param name="commandName">The command the lines report under.</param>
    internal static ClaimTerms Claims(string commandName) => new()
    {
        CommandName = commandName,
        OwnerSuffix = ClaimSuffix,
        Directory = worker => $"the mutation worker '{worker}'",
        OwnerFile = "The mutation worker's claim file",
        Unwritten = "Until it can be, two sweeps could mutate one worker's copy.",
        Unreadable = "Remove it once no sweep is using that worker.",
        Beside = "The mutation workers beside",
        Held = worker => $"the mutation worker at '{worker}'",
        Abandoned = "so a site it had mutated there may still be mutated, until the worker's copy is synced again, as it is before any sweep uses it.",
    };

    /// <summary>
    /// Which sweep is using a worker's copy on this machine, as whatever removes the workers kept beside a tree asks
    /// before it removes one.
    /// </summary>
    /// <param name="fileSystem">Reads the claims, and removes one whose copy is gone.</param>
    /// <param name="output">Where a claim's own lines go.</param>
    /// <param name="identity">Tells a sweep still running from one that has ended.</param>
    public static ICopyClaims CopyClaims(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity)
        => new WorkerClaims(fileSystem, output, identity, MutationService.CommandName);

    /// <summary>
    /// The key <paramref name="variant"/>'s workers are named by: the first <see cref="WorkerFamily.KeyLength"/>
    /// hexadecimal digits, in lower case, of the SHA-256 of the name its build directory is kept under.
    /// </summary>
    /// <param name="variant">The leg's variant.</param>
    /// <remarks>
    /// Derived from the name alone, so the machine that dispatches a leg and the host that sweeps it name its workers
    /// alike. Two variants of one tree whose names come to one key - a chance in hundreds of millions for a pair - share
    /// their workers and their lock, and neither is judged on the other's: each sweep syncs a worker again by content
    /// and builds in its own variant's directory within it, and a sweep of one holds off the other's, saying whose it is.
    /// </remarks>
    public static string KeyOf(VariantKey variant)
    {
        ArgumentNullException.ThrowIfNull(variant);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(variant.DirectoryName)))[..WorkerFamily.KeyLength];
    }

    /// <summary>The family of workers a sweep of <paramref name="variant"/> keeps beside <paramref name="treeRoot"/>, or its self-test does.</summary>
    /// <param name="treeRoot">The tree the workers are kept beside.</param>
    /// <param name="variant">The leg's variant.</param>
    /// <param name="selfTest">Whether the family is the self-test's.</param>
    public static WorkerFamily Of(string treeRoot, VariantKey variant, bool selfTest = false)
        => new(treeRoot, KeyOf(variant), selfTest);

    /// <summary>Where worker <paramref name="number"/> of <paramref name="variant"/> copies <paramref name="treeRoot"/>.</summary>
    /// <param name="treeRoot">The tree the worker copies.</param>
    /// <param name="variant">The leg's variant.</param>
    /// <param name="number">The worker's number, from 1.</param>
    public static string PathOf(string treeRoot, VariantKey variant, int number) => Of(treeRoot, variant).PathOf(number);

    /// <summary>
    /// The family and the number of the worker kept under <paramref name="name"/> in the mutation family - what follows
    /// <see cref="HostCopies.MutationSuffix"/> - or <see langword="null"/> where it is spelt as no worker: its family's
    /// name (<see cref="WorkerFamily.Named"/>), a hyphen and its number, in digits alone and never a leading zero, so a
    /// worker's name is spelt one way.
    /// </summary>
    /// <param name="name">The name the copy is kept under.</param>
    public static (string Family, int Number)? Named(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var hyphen = name.LastIndexOf('-');

        // Digits alone, as NumberStyles.None reads them.
        return hyphen > 0
            && hyphen < name.Length - 1
            && name[hyphen + 1] != '0'
            && int.TryParse(name[(hyphen + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && WorkerFamily.Named(string.Empty, name[..hyphen]) is not null
            ? (name[..hyphen], number)
            : null;
    }

    /// <summary>
    /// The name of the tree a directory named <paramref name="name"/> is a mutation worker of - what comes before the
    /// last <see cref="HostCopies.MutationSuffix"/> in it, a worker's name holding none - or <see langword="null"/>
    /// where it is named as no worker. So a worker kept beside a worktree, a copy with a repository of its own, is never
    /// taken for a worktree.
    /// </summary>
    /// <param name="name">A directory's own name, with no path before it.</param>
    public static string? TreeNamed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var at = name.LastIndexOf(HostCopies.MutationSuffix, StringComparison.Ordinal);

        return at > 0 && Named(name[(at + HostCopies.MutationSuffix.Length)..]) is not null ? name[..at] : null;
    }

    /// <summary>
    /// The trees that have a mutation worker in <paramref name="directory"/>, by the names of their own directories
    /// there: each once, however many workers it has, in order; none where the directory is not there. Told by name
    /// alone (<see cref="TreeNamed"/>): whether a directory so named is one a sync made is what removing a tree's
    /// workers reads, and says.
    /// </summary>
    /// <param name="fileSystem">Lists the directory.</param>
    /// <param name="directory">The directory trees and their workers are kept in, side by side.</param>
    public static IReadOnlyList<string> TreesWithWorkersIn(IFileSystem fileSystem, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return !fileSystem.DirectoryExists(directory)
            ? []
            : [.. fileSystem.EnumerateDirectories(directory)
                .Select(found => TreeNamed(Path.GetFileName(Path.TrimEndingDirectorySeparator(found))))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The lock a sweep of <paramref name="leg"/> takes: its workers' key, whole, on the leg's host - which a build of the
    /// leg, keyed by its tree, never meets.
    /// </summary>
    /// <param name="leg">The leg swept.</param>
    /// <param name="runId">The run taking it.</param>
    /// <param name="command">The command taking it.</param>
    public static LockRequest SweepLock(PlacedLeg leg, RunId runId, string command)
    {
        ArgumentNullException.ThrowIfNull(leg);

        return SweepLock(leg.Host.Host, leg.HostTreeRoot, leg.Variant, runId, command);
    }

    /// <summary>The lock a sweep of <paramref name="variant"/> in <paramref name="hostTreeRoot"/> on <paramref name="host"/> takes.</summary>
    /// <param name="host">The host the leg is swept on.</param>
    /// <param name="hostTreeRoot">The tree there.</param>
    /// <param name="variant">The leg's variant.</param>
    /// <param name="runId">The run taking it.</param>
    /// <param name="command">The command taking it.</param>
    public static LockRequest SweepLock(HostId host, string hostTreeRoot, VariantKey variant, RunId runId, string command)
        => SweepLock(host, Of(hostTreeRoot, variant), runId, command);

    /// <summary>The lock a sweep of <paramref name="family"/>'s variant on <paramref name="host"/> takes, its self-test with it.</summary>
    /// <param name="host">The host the leg is swept on.</param>
    /// <param name="family">A family of the variant's workers, its own or its self-test's.</param>
    /// <param name="runId">The run taking it.</param>
    /// <param name="command">The command taking it.</param>
    public static LockRequest SweepLock(HostId host, WorkerFamily family, RunId runId, string command)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(family);

        return new LockRequest
        {
            Host = host.ToString(),
            Tree = family.SweepKey,
            Scope = LockScope.TreeExclusive,
            RunId = runId,
            Command = command,
        };
    }
}
