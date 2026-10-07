using System.Globalization;
using RepoHarness.Core.Build;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// Where a leg's mutation workers are kept, and the lock a sweep of the leg takes: each worker a copy of the leg's tree
/// beside it, named for the leg's variant and the worker's number, in a family of copies of its own.
/// </summary>
/// <remarks>
/// <para>
/// A worker is <c>&lt;tree&gt;.mutation-&lt;variant&gt;-&lt;n&gt;</c>, numbered from 1, its build directory the variant's
/// own within it, as the leg's is within the tree. It stays between sweeps, synced again by content before each, so its
/// build is warm: the sweep only ever reads the tree, and builds and mutates in its workers alone.
/// </para>
/// <para>
/// A sweep of a leg variant is locked by <see cref="SweepLock(PlacedLeg, RunId, string)"/>: what its workers are kept under, spelt as one more copy
/// beside the tree would be, which no copy is - so it conflicts with another sweep of the leg, and with a clean of its
/// workers, and never with a build, test or sync of the leg, which a sweep of hours must never hold off.
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

    /// <summary>The key a sweep of <paramref name="variant"/> in <paramref name="treeRoot"/> is locked by, and its workers are named after.</summary>
    /// <param name="treeRoot">The tree the workers copy.</param>
    /// <param name="variant">The leg's variant.</param>
    public static string RootOf(string treeRoot, VariantKey variant)
    {
        ArgumentNullException.ThrowIfNull(variant);

        return HostCopies.InFamily(treeRoot, HostCopies.MutationSuffix, variant.DirectoryName);
    }

    /// <summary>Where worker <paramref name="number"/> of <paramref name="variant"/> copies <paramref name="treeRoot"/>.</summary>
    /// <param name="treeRoot">The tree the worker copies.</param>
    /// <param name="variant">The leg's variant.</param>
    /// <param name="number">The worker's number, from 1.</param>
    public static string PathOf(string treeRoot, VariantKey variant, int number)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        return RootOf(treeRoot, variant) + "-" + number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The number of the worker of <paramref name="variant"/> kept under <paramref name="name"/> in the mutation family -
    /// what follows <see cref="HostCopies.MutationSuffix"/> - or <see langword="null"/> where it is no worker of that
    /// variant: another variant's, whose name starts alike and goes on, or no worker at all.
    /// </summary>
    /// <param name="name">The name the copy is kept under.</param>
    /// <param name="variant">The leg's variant.</param>
    public static int? NumberOf(string name, VariantKey variant)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(variant);

        var prefix = variant.DirectoryName + "-";

        // Digits alone, as NumberStyles.None reads them, and never a leading zero: a worker's name is spelt one way.
        return name.StartsWith(prefix, StringComparison.Ordinal)
            && name.Length > prefix.Length
            && name[prefix.Length] != '0'
            && int.TryParse(name[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
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
    {
        ArgumentNullException.ThrowIfNull(host);

        return new LockRequest
        {
            Host = host.ToString(),
            Tree = RootOf(hostTreeRoot, variant),
            Scope = LockScope.TreeExclusive,
            RunId = runId,
            Command = command,
        };
    }
}
