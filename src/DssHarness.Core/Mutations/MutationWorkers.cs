using System.Globalization;
using RepoHarness.Core.Build;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// One family of the mutation workers kept in a tree: those a sweep of one variant keeps there, or those a self-test
/// of that variant does - each <c>&lt;tree&gt;/.mutations/&lt;key&gt;&lt;mark&gt;-&lt;n&gt;</c>, numbered from 1: the
/// key its variant's (<see cref="VariantKey.CopyKey"/>), the mark <see cref="OwnMark"/> for the variant's own workers
/// and <see cref="SelfTestMark"/> for its self-test's.
/// </summary>
/// <remarks>
/// A key, never the variant's name: a worker is a second tree, built as deep as the first, so on a machine whose paths
/// are bounded every character its name adds is one the tree's own path must leave free. Spelt out, a variant's name
/// stood in a worker's path twice - in its name, and in the build directory within it - and a repository whose path
/// budget is reckoned to the character had room for neither. A worker's name adds the same few characters whatever
/// its variant is called, and which leg a worker is the lines of the sweep that makes it, and of the clean that
/// removes it, say.
/// </remarks>
public sealed record WorkerFamily
{
    /// <summary>What follows the key in the name of a variant's own workers, which its sweep drives its arms in.</summary>
    public const char OwnMark = 'w';

    /// <summary>What follows the key in the name of the workers of a variant's self-test.</summary>
    public const char SelfTestMark = 's';

    private WorkerFamily(string treeRoot, string key, bool selfTest)
    {
        TreeRoot = treeRoot;
        Key = key;
        SelfTest = selfTest;
    }

    /// <summary>The tree the workers are kept in.</summary>
    public string TreeRoot { get; }

    /// <summary>The variant's key: <see cref="VariantKey.CopyKeyLength"/> hexadecimal digits, in lower case.</summary>
    public string Key { get; }

    /// <summary>Whether they are a self-test's, copies of the fixture this tool carries rather than of the tree.</summary>
    public bool SelfTest { get; }

    /// <summary>The name its workers are kept under, before each one's number: the variant's key, then the family's mark.</summary>
    public string Name => Key + (SelfTest ? SelfTestMark : OwnMark);

    /// <summary>
    /// How many characters the first worker of a family adds to its tree's path: the separator, the slot, the separator,
    /// the family's name, and <c>-1</c>. Kept in the tree, a worker is <see cref="DeeperThanBeside"/> characters longer
    /// than one kept beside it was.
    /// </summary>
    public static int AddedToItsTreesPath { get; } = 1 + HarnessLayout.MutationsDirectoryName.Length + 1 + VariantKey.CopyKeyLength + 1 + "-1".Length;

    /// <summary>
    /// How many characters deeper a worker is kept than one an earlier version of this tool kept beside its tree, as
    /// <c>&lt;tree&gt;.mutation-&lt;key&gt;&lt;mark&gt;-&lt;n&gt;</c>: what a tree that fitted such a worker within the
    /// path limit, and fits no worker kept in it, lacks.
    /// </summary>
    public static int DeeperThanBeside { get; } = AddedToItsTreesPath - (".mutation-".Length + VariantKey.CopyKeyLength + 1 + "-1".Length);

    /// <summary>What every worker of the family is named after: its path, before the hyphen and the worker's number.</summary>
    public string Root => HarnessLayout.InSlot(TreeRoot, HarnessLayout.MutationsDirectoryName, Name);

    /// <summary>
    /// The key a sweep of the variant is locked by, its self-test with it: the variant's key with no mark, in the slot,
    /// which no worker is named, so a sweep and a self-test of one leg never run at once.
    /// </summary>
    public string SweepKey => HarnessLayout.InSlot(TreeRoot, HarnessLayout.MutationsDirectoryName, Key);

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
    /// <param name="treeRoot">The tree the workers are kept in.</param>
    /// <param name="name">The name the workers are kept under, before each one's number.</param>
    public static WorkerFamily? Named(string treeRoot, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        const int KeyLength = VariantKey.CopyKeyLength;

        return name.Length == KeyLength + 1
            && VariantKey.IsCopyKey(name.AsSpan(0, KeyLength))
            && name[KeyLength] is OwnMark or SelfTestMark
            ? new WorkerFamily(treeRoot, name[..KeyLength], name[KeyLength] == SelfTestMark)
            : null;
    }

    /// <summary>
    /// The family of <paramref name="treeRoot"/> keyed <paramref name="key"/>: made of a key that is one, and of no
    /// other, since its workers' names are read back by that spelling (<see cref="Named"/>).
    /// </summary>
    /// <param name="treeRoot">The tree the workers are kept in.</param>
    /// <param name="key">The variant's key (<see cref="VariantKey.CopyKey"/>).</param>
    /// <param name="selfTest">Whether the family is the self-test's.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is spelt as no key.</exception>
    public static WorkerFamily Of(string treeRoot, string key, bool selfTest = false)
    {
        ArgumentNullException.ThrowIfNull(treeRoot);
        ArgumentNullException.ThrowIfNull(key);

        return VariantKey.IsCopyKey(key)
            ? new WorkerFamily(treeRoot, key, selfTest)
            : throw new ArgumentException($"'{key}' is no key of a worker family: {VariantKey.CopyKeyLength} hexadecimal digits, in lower case.", nameof(key));
    }
}

/// <summary>
/// Where a leg's mutation workers are kept, and the lock a sweep of the leg takes: each worker a copy of the leg's tree
/// kept in the tree's own slot for them (<see cref="HarnessLayout.MutationsDirectoryName"/>), named by the key of the
/// leg's variant and the worker's number.
/// </summary>
/// <remarks>
/// <para>
/// A worker is <c>&lt;tree&gt;/.mutations/&lt;key&gt;w-&lt;n&gt;</c>, numbered from 1, its build directory the
/// variant's own within it, as the leg's is within the tree; a repository of its own, as every copy a sync makes is, so
/// git and the harness answer there for the worker, never for the tree around it. It stays between sweeps, synced again
/// by content before each, so its build is warm: the sweep reads the tree's own files and writes none of them, and builds
/// and mutates in its workers alone. A self-test's workers are kept the same way, in a family of their own
/// (<see cref="WorkerFamily.SelfTestMark"/>): copies of the fixture this tool carries, in the tree of the leg whose
/// toolchain builds them.
/// </para>
/// <para>
/// A sweep of a leg variant is locked by <see cref="SweepLock(PlacedLeg, RunId, string)"/>: what its workers are kept under, spelt as one more copy
/// in the slot would be, which no copy is - so it conflicts with another sweep of the leg, and with a clean of its
/// workers, and never with a build, test or sync of the leg, which a sweep of hours must never hold off.
/// </para>
/// <para>
/// The workers go with their tree: whatever removes a worktree, or a host's copy of one, removes the workers kept in it
/// first, and leaves the tree where a sweep still running holds one of them. Workers an earlier version of this tool
/// kept beside a tree are known to nothing here, and are a person's to delete.
/// </para>
/// </remarks>
public static class MutationWorkers
{
    /// <summary>The suffix of a worker's claim file, beside the worker's copy: which run's sweep is using it.</summary>
    public const string ClaimSuffix = ".claim.json";

    /// <summary>What a line calls one worker, as the claims on workers say it (<see cref="ICopyClaims.Member"/>).</summary>
    public const string Member = "mutation worker";

    /// <summary>
    /// How a line says a sweep still running holds a worker, as the claims on workers say it
    /// (<see cref="ICopyClaims.SaidHeld"/>).
    /// </summary>
    /// <param name="holder">The sweep, as <see cref="ICopyClaims.HeldBy"/> names it.</param>
    public static string SaidHeld(string holder) => $"a sweep still running holds it: {holder}";

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
    /// Which sweep is using a worker's copy on this machine, as whatever removes the workers kept in a tree asks before it
    /// removes one.
    /// </summary>
    /// <param name="fileSystem">Reads the claims, and removes one whose copy is gone.</param>
    /// <param name="output">Where a claim's own lines go.</param>
    /// <param name="identity">Tells a sweep still running from one that has ended.</param>
    public static ICopyClaims CopyClaims(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity)
        => new WorkerClaims(fileSystem, output, identity, MutationService.CommandName);

    /// <summary>The family of workers a sweep of <paramref name="variant"/> keeps in <paramref name="treeRoot"/>, or its self-test does.</summary>
    /// <param name="treeRoot">The tree the workers are kept in.</param>
    /// <param name="variant">The leg's variant.</param>
    /// <param name="selfTest">Whether the family is the self-test's.</param>
    public static WorkerFamily Of(string treeRoot, VariantKey variant, bool selfTest = false)
    {
        ArgumentNullException.ThrowIfNull(variant);

        return WorkerFamily.Of(treeRoot, variant.CopyKey, selfTest);
    }

    /// <summary>Where worker <paramref name="number"/> of <paramref name="variant"/> copies <paramref name="treeRoot"/>.</summary>
    /// <param name="treeRoot">The tree the worker copies.</param>
    /// <param name="variant">The leg's variant.</param>
    /// <param name="number">The worker's number, from 1.</param>
    public static string PathOf(string treeRoot, VariantKey variant, int number) => Of(treeRoot, variant).PathOf(number);

    /// <summary>
    /// The family and the number of the worker kept under <paramref name="name"/> in a tree's slot for workers, or
    /// <see langword="null"/> where it is spelt as no worker: its family's name (<see cref="WorkerFamily.Named"/>), a
    /// hyphen and its number, in digits alone and never a leading zero, so a worker's name is spelt one way.
    /// </summary>
    /// <param name="name">The name the copy is kept under in the slot.</param>
    public static (string Family, int Number)? Named(string name)
        => Named(string.Empty, name) is { } worker ? (worker.Family.Name, worker.Number) : null;

    /// <summary>
    /// The worker kept under <paramref name="name"/> in <paramref name="treeRoot"/>, as <see cref="Named(string)"/> reads
    /// its name: its family, of that tree, and its number.
    /// </summary>
    /// <param name="treeRoot">The tree the worker is kept in.</param>
    /// <param name="name">The name the copy is kept under in the slot.</param>
    public static (WorkerFamily Family, int Number)? Named(string treeRoot, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var hyphen = name.LastIndexOf('-');

        // Digits alone, as NumberStyles.None reads them.
        return hyphen > 0
            && hyphen < name.Length - 1
            && name[hyphen + 1] != '0'
            && int.TryParse(name[(hyphen + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && WorkerFamily.Named(treeRoot, name[..hyphen]) is { } family
            ? (family, number)
            : null;
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
