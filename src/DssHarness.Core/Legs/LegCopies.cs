using DssHarness.Core.Build;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Repository;
using DssHarness.Core.Runs;
using DssHarness.Core.Sync;

namespace DssHarness.Core.Legs;

/// <summary>
/// The copies of a tree its legs work in, kept in the tree's own slot for them
/// (<see cref="HarnessLayout.LegsDirectoryName"/>): each named by the key of the variant the leg working in it builds.
/// </summary>
/// <remarks>
/// <para>
/// One copy per tree and variant, so the same variant excludes itself, as it does in the tree, and nothing else: a leg
/// working in one holds it shared with its variant, the sync that replaces it whole, and no lock of the leg's reaches the
/// tree - a fold, a rebase, a refresh, a commit or an edit meet nothing of the leg's, and the leg ends on the tree its run
/// read. Its build directory is the variant's own within it, kept warm between runs; a runner's steps write into the copy,
/// and what they keep under <c>outputs</c> with <c>persist</c> is kept there, on this machine, named on the leg's line.
/// </para>
/// <para>
/// A copy needs no claim file: one run lock key covers one copy, where a family of mutation workers shares one key among
/// several. Whatever removes the copies with their tree asks the lock (<see cref="Claims"/>), which says a run holds one
/// as the claims on workers say a sweep holds one of them.
/// </para>
/// </remarks>
public static class LegCopies
{
    /// <summary>Where the copy <paramref name="variant"/> builds in is kept: in the tree's own slot for them, named by the variant's key.</summary>
    /// <param name="tree">The tree the copy is of.</param>
    /// <param name="variant">The variant the leg working in the copy builds.</param>
    public static string PathOf(string tree, VariantKey variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);

        return HarnessLayout.InSlot(tree, HarnessLayout.LegsDirectoryName, variant.CopyKey);
    }

    /// <summary>Whether <paramref name="name"/> is spelt as a copy's is in the slot: a variant's key, and nothing else.</summary>
    /// <param name="name">A directory's name in the slot.</param>
    public static bool IsKey(string name) => VariantKey.IsCopyKey(name);

    /// <summary>What a line calls one copy of the family, in the singular (<see cref="ICopyClaims.Member"/>).</summary>
    public const string Member = "leg copy";

    /// <summary>How a line says a run still running holds a copy of the family (<see cref="ICopyClaims.SaidHeld"/>).</summary>
    /// <param name="holder">The run, as <see cref="CopyHolder.Said"/> names it.</param>
    public static string SaidHeld(string holder) => $"a run still running holds it: {holder}";

    /// <summary>
    /// What whatever would remove a copy of the family asks the run lock first: the copy taken whole on this machine, so
    /// a run holding it any way - building in it, or replacing it - is named. The run it names is the asking's own, new,
    /// and the command the one asking - clean, or a deletion of the tree, whose command asks it as the removal it is
    /// (<see cref="RunLock.RemovalCommand"/>), so a run holding anything kept inside the copy is named too - and asking
    /// writes no entry.
    /// </summary>
    /// <param name="copy">The copy.</param>
    /// <param name="command">The command asking.</param>
    public static LockRequest Whole(string copy, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(copy);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        return new LockRequest
        {
            Host = HostId.Local.ToString(),
            Tree = copy,
            Scope = LockScope.TreeExclusive,
            RunId = RunId.New(),
            Command = command,
        };
    }

    /// <summary>
    /// Which run holds a copy of the family, as whatever would remove it asks first: the run lock's answer for the copy
    /// taken whole (<see cref="Whole"/>), so a run building in one, or replacing it, keeps it.
    /// </summary>
    /// <param name="runLock">Reads the run lock of the repository the tree belongs to.</param>
    /// <param name="layout">The repository, whose main checkout holds the lock file.</param>
    /// <param name="command">The command asking, which the lock is asked under (<see cref="Whole"/>).</param>
    public static ICopyClaims Claims(RunLock runLock, HarnessLayout layout, string command)
    {
        ArgumentNullException.ThrowIfNull(runLock);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        return new LegCopyClaims(runLock, layout, command);
    }

    /// <summary>How a copy of the family's name reads, as its removal says one it left aside.</summary>
    private sealed class LegCopyClaims(RunLock runLock, HarnessLayout layout, string command) : ICopyClaims
    {
        public bool Names(string name) => IsKey(name);

        public string Member => LegCopies.Member;

        public string SaidHeld(string holder) => LegCopies.SaidHeld(holder);

        public CopyHolder? HeldBy(string copy) => runLock.HeldBy(layout, Whole(copy, command)) is { } said ? new CopyHolder(said, Claim: null) : null;

        public void Forget(string copy)
        {
            // The lock entries of a run that has ended are reclaimed by the lock itself; a removal writes none.
        }
    }
}
