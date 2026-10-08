using System.Globalization;
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
/// of that variant does - each <c>&lt;tree&gt;.mutation-&lt;name&gt;-&lt;n&gt;</c>, numbered from 1, the name the
/// variant's own or, for a self-test's, <see cref="SelfTestPrefix"/> and the variant's.
/// </summary>
/// <param name="TreeRoot">The tree the workers are kept beside.</param>
/// <param name="Variant">The variant's directory name.</param>
/// <param name="SelfTest">Whether they are a self-test's, copies of the fixture this tool carries rather than of the tree.</param>
public sealed record WorkerFamily(string TreeRoot, string Variant, bool SelfTest = false)
{
    /// <summary>What a self-test's family adds before its variant's name, which no variant's starts with: a processor's name comes first in one.</summary>
    public const string SelfTestPrefix = "self-test-";

    /// <summary>The name its workers are kept under, before each one's number.</summary>
    public string Name => SelfTest ? SelfTestPrefix + Variant : Variant;

    /// <summary>What every worker of the family is named after: its path, before the hyphen and the worker's number.</summary>
    public string Root => HostCopies.InFamily(TreeRoot, HostCopies.MutationSuffix, Name);

    /// <summary>
    /// The key a sweep of the variant is locked by, its self-test with it: what the variant's own workers are named
    /// after, so a sweep and a self-test of one leg never run at once.
    /// </summary>
    public string SweepKey => HostCopies.InFamily(TreeRoot, HostCopies.MutationSuffix, Variant);

    /// <summary>Where worker <paramref name="number"/> of the family is kept.</summary>
    /// <param name="number">The worker's number, from 1.</param>
    public string PathOf(int number)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        return Root + "-" + number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The family of <paramref name="treeRoot"/> whose workers are kept under <paramref name="name"/>.</summary>
    /// <param name="treeRoot">The tree the workers are kept beside.</param>
    /// <param name="name">The name the workers are kept under, before each one's number.</param>
    public static WorkerFamily Named(string treeRoot, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.StartsWith(SelfTestPrefix, StringComparison.Ordinal) && name.Length > SelfTestPrefix.Length
            ? new WorkerFamily(treeRoot, name[SelfTestPrefix.Length..], SelfTest: true)
            : new WorkerFamily(treeRoot, name);
    }
}

/// <summary>
/// Where a leg's mutation workers are kept, and the lock a sweep of the leg takes: each worker a copy beside the leg's
/// tree, named for the leg's variant and the worker's number, in a family of copies of its own.
/// </summary>
/// <remarks>
/// <para>
/// A worker is <c>&lt;tree&gt;.mutation-&lt;variant&gt;-&lt;n&gt;</c>, numbered from 1, its build directory the variant's
/// own within it, as the leg's is within the tree. It stays between sweeps, synced again by content before each, so its
/// build is warm: the sweep only ever reads the tree, and builds and mutates in its workers alone. A self-test's workers
/// are kept the same way, in a family of their own (<see cref="WorkerFamily.SelfTestPrefix"/>): copies of the fixture
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

    /// <summary>The family of workers a sweep of <paramref name="variant"/> keeps beside <paramref name="treeRoot"/>, or its self-test does.</summary>
    /// <param name="treeRoot">The tree the workers are kept beside.</param>
    /// <param name="variant">The leg's variant.</param>
    /// <param name="selfTest">Whether the family is the self-test's.</param>
    public static WorkerFamily Of(string treeRoot, VariantKey variant, bool selfTest = false)
    {
        ArgumentNullException.ThrowIfNull(variant);

        return new WorkerFamily(treeRoot, variant.DirectoryName, selfTest);
    }

    /// <summary>Where worker <paramref name="number"/> of <paramref name="variant"/> copies <paramref name="treeRoot"/>.</summary>
    /// <param name="treeRoot">The tree the worker copies.</param>
    /// <param name="variant">The leg's variant.</param>
    /// <param name="number">The worker's number, from 1.</param>
    public static string PathOf(string treeRoot, VariantKey variant, int number) => Of(treeRoot, variant).PathOf(number);

    /// <summary>
    /// The family and the number of the worker kept under <paramref name="name"/> in the mutation family - what follows
    /// <see cref="HostCopies.MutationSuffix"/> - or <see langword="null"/> where it is spelt as no worker: its name ends
    /// in a hyphen and its number, in digits alone and never a leading zero, so a worker's name is spelt one way.
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
            ? (name[..hyphen], number)
            : null;
    }

    /// <summary>
    /// The name of the tree a directory named <paramref name="name"/> is a mutation worker of - what comes before
    /// <see cref="HostCopies.MutationSuffix"/> in it - or <see langword="null"/> where it is named as no worker. So a
    /// worker kept beside a worktree, a copy with a repository of its own, is never taken for a worktree.
    /// </summary>
    /// <param name="name">A directory's own name, with no path before it.</param>
    public static string? TreeNamed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var at = name.IndexOf(HostCopies.MutationSuffix, StringComparison.Ordinal);

        return at > 0 && Named(name[(at + HostCopies.MutationSuffix.Length)..]) is not null ? name[..at] : null;
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
