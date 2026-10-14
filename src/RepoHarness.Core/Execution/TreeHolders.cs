using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>Where a holder's leg stands, from this machine's record of its heavy legs.</summary>
public enum HolderStanding
{
    /// <summary>It asked for no slot: it runs.</summary>
    NoSlot,

    /// <summary>It is in line, behind the legs holding slots: it has not started.</summary>
    Waiting,

    /// <summary>It holds a slot: it runs, or starts as soon as the memory and the room allow.</summary>
    HoldsSlot,

    /// <summary>It is a leg on an ssh host, whose own record says whether it runs or waits.</summary>
    OnHost,
}

/// <summary>
/// One run in the way of a dry run's <c>--apply</c>: a tree the apply takes, or a copy of the worktree a deletion
/// removes - the lock's entry for it, where its leg stands, and the one sentence that says it.
/// </summary>
/// <param name="Copy">
/// Whether what it holds is a copy of the worktree a deletion removes, which leaves that removal unfinished (exit 21),
/// rather than a tree the apply takes, which refuses it (exit 13).
/// </param>
/// <param name="Entry">The lock's entry for what it holds.</param>
/// <param name="Standing">Where its leg stands; <see langword="null"/> where the record of heavy legs could not be read.</param>
/// <param name="Ahead">How many legs are ahead of it in line, where it waits.</param>
/// <param name="Said">The one sentence of it, as a <c>held now:</c> line and a hold's refusal say it.</param>
public sealed record TreeHolder(bool Copy, LockEntry Entry, HolderStanding? Standing, int? Ahead, string Said);

/// <summary>What a dry run read of who holds what its <c>--apply</c> takes.</summary>
/// <param name="Holders">Every holder named from the run lock, in the lock file's order.</param>
/// <param name="Unread">Why the lock file could not be read, where it could not; the holders are none then.</param>
/// <param name="StandingUnread">
/// Why the record of heavy legs could not be read, where it could not: each holder is still named, without the words
/// that say where its leg stands.
/// </param>
/// <param name="CopyLines">
/// What the families of copies kept in the worktree say would be left - a leg copy or a mutation worker a run or a
/// sweep holds, each in its family's own words - where a deletion's dry run asked them; empty where it asked nobody.
/// </param>
public sealed record TreeHeld(IReadOnlyList<TreeHolder> Holders, string? Unread, string? StandingUnread, IReadOnlyList<string> CopyLines)
{
    /// <summary>No holder was named.</summary>
    public static TreeHeld None { get; } = new([], null, null, []);

    /// <summary>Whether a run holds a copy the deletion removes, from the lock or from the families' own words.</summary>
    public bool HoldsCopies => Holders.Any(holder => holder.Copy) || CopyLines.Count > 0;

    /// <summary>Whether a run holds a tree the apply takes, which refuses it.</summary>
    public bool HoldsATree => Holders.Any(holder => !holder.Copy);

    /// <summary>Whether a run holds only copies, which a deletion leaves unfinished.</summary>
    public bool HoldsOnlyCopies => (Holders.Count > 0 || CopyLines.Count > 0) && !HoldsATree;
}

/// <summary>
/// Who holds what a command's <c>--apply</c> takes: the run lock asked with the very requests the apply takes, joined
/// with this machine's record of heavy legs, so each holder is named with where its leg stands. Reads both, writes
/// neither and reclaims nothing.
/// </summary>
/// <param name="runLock">The lock, whose live entries in the way name the holders.</param>
/// <param name="slots">The record of heavy legs, whose line says where a holder's leg stands.</param>
/// <param name="identity">This process, which tells this machine from another and spells the holder.</param>
public sealed class TreeHolders(RunLock runLock, HeavyLegSlots slots, IProcessIdentity identity)
{
    /// <summary>
    /// Who holds what <paramref name="takes"/> and <paramref name="removes"/> name: every live entry in the way of any
    /// of them, each with where its leg stands. Nothing is taken, written or reclaimed.
    /// </summary>
    /// <param name="layout">The repository, whose main checkout holds the lock file.</param>
    /// <param name="takes">The trees the apply takes, which a run holding refuses it.</param>
    /// <param name="removes">The copies a deletion removes, which a run holding leaves unfinished.</param>
    /// <returns>What was read; <see cref="TreeHeld.Unread"/> where the lock file could not be read.</returns>
    public TreeHeld Read(HarnessLayout layout, IReadOnlyList<LockRequest> takes, IReadOnlyList<LockRequest> removes)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(takes);
        ArgumentNullException.ThrowIfNull(removes);

        IReadOnlyList<LockEntry> entries;

        try
        {
            entries = _runLock.Holders(layout, [.. takes, .. removes]);
        }
        catch (HarnessException ex)
        {
            return new TreeHeld([], ex.Message.TrimEnd('.'), null, []);
        }

        return Joined(layout, takes, [.. entries.Select(entry => (Entry: entry, Copy: removes.Any(request => RunLockMatches(entry, request))))]);
    }

    /// <summary>
    /// Who holds what <paramref name="takes"/> names, beside the copies of the worktree <paramref name="copies"/> were
    /// read holding - the lock asked for those by their own requests, as their removal asks it: every holder with where
    /// its leg stands, each copy marked as the copy it is. Nothing is taken, written or reclaimed.
    /// </summary>
    /// <param name="layout">The repository, whose main checkout holds the lock file.</param>
    /// <param name="takes">The trees the apply takes, which a run holding refuses it.</param>
    /// <param name="copies">Who holds a copy the deletion removes, as the lock answered for each copy's path.</param>
    /// <returns>What was read; <see cref="TreeHeld.Unread"/> where the lock file could not be read.</returns>
    public TreeHeld Read(HarnessLayout layout, IReadOnlyList<LockRequest> takes, IReadOnlyList<LockEntry> copies)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(takes);
        ArgumentNullException.ThrowIfNull(copies);

        try
        {
            var trees = _runLock.Holders(layout, takes);

            return Joined(layout, takes, [.. trees.Select(entry => (Entry: entry, Copy: false)), .. copies.Select(entry => (Entry: entry, Copy: true))]);
        }
        catch (HarnessException ex)
        {
            return new TreeHeld([], ex.Message.TrimEnd('.'), null, []);
        }
    }

    /// <summary>Every holder joined with the line: each with where its leg stands and the sentence of it.</summary>
    private TreeHeld Joined(HarnessLayout layout, IReadOnlyList<LockRequest> takes, IReadOnlyList<(LockEntry Entry, bool Copy)> entries)
    {
        ArgumentNullException.ThrowIfNull(layout);

        IReadOnlyList<SlotEntry> line;
        string? standingUnread = null;

        try
        {
            line = _slots.Line();
        }
        catch (HarnessException ex)
        {
            line = [];
            standingUnread = ex.Message.TrimEnd('.');
        }

        return new TreeHeld([.. entries.Select(entry => Holder(entry.Entry, line, entry.Copy))], null, standingUnread, []);
    }

    /// <summary>
    /// The refusal a hold gives where <paramref name="holder"/> stood in its way: the lock's own sentence, and where
    /// that run's leg stands - the same words a dry run's <c>held now:</c> line says after the tree it names.
    /// </summary>
    /// <param name="wanted">What was asked for.</param>
    /// <param name="holder">Who held it.</param>
    public string Refusal(LockRequest wanted, LockEntry holder)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(holder);

        IReadOnlyList<SlotEntry> line;

        try
        {
            line = _slots.Line();
        }
        catch (HarnessException)
        {
            line = [];
        }

        var standing = Standing(holder, line, out var ahead);

        return $"{RunLock.TreeNamed(wanted.Host, wanted.Tree, wanted.Variant)} is held by {Held(holder, Tail(standing, ahead))}.";
    }

    /// <summary>One holder: the lock's entry, joined with the line, and the sentence of it.</summary>
    private TreeHolder Holder(LockEntry entry, IReadOnlyList<SlotEntry> line, bool copy)
    {
        var standing = Standing(entry, line, out var ahead);

        return new TreeHolder(
            copy,
            entry,
            standing,
            standing == HolderStanding.Waiting ? ahead : null,
            $"'{entry.Tree}' on {entry.Host}{(entry.Variant is { } variant ? $" variant '{variant}'" : string.Empty)} is held by {Held(entry, Tail(standing, ahead))}.");
    }

    /// <summary>
    /// The lock's own part of the sentence, unchanged: who holds it, since when, running what - on the tail of where its
    /// leg stands.
    /// </summary>
    private string Held(LockEntry entry, string tail)
        => _identity.Describe(entry.Holder.Machine, entry.Holder.ProcessId, entry.Holder.RunId, entry.Holder.TakenUtc)
            + $", running '{entry.Holder.Command}'{ProcessHolders.OlderBuildNote(entry.Holder.ProcessStamp)}"
            + _identity.ElsewhereNote(entry.Holder.Machine)
            + tail;

    /// <summary>
    /// Where the leg of <paramref name="entry"/>'s run stands: it holds a slot where any of its places in the line does,
    /// it waits where the line holds it further back, and where the lock names an ssh host, that host's own record says.
    /// </summary>
    private HolderStanding? Standing(LockEntry entry, IReadOnlyList<SlotEntry> line, out int ahead)
    {
        ahead = 0;

        var places = line
            .Select((slot, at) => (slot, at))
            .Where(place => string.Equals(place.slot.RunId, entry.Holder.RunId, StringComparison.Ordinal)
                && place.slot.ProcessId == entry.Holder.ProcessId
                && string.Equals(place.slot.Host, entry.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(place.slot.Tree, entry.Tree, StringComparison.OrdinalIgnoreCase)
                && string.Equals(place.slot.Variant ?? string.Empty, entry.Variant ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (places.Count == 0)
        {
            return HostId.TryParse(entry.Host, out var host) && host.Kind == HostKind.Ssh ? HolderStanding.OnHost : HolderStanding.NoSlot;
        }

        var holding = Holding(line);
        ahead = places.Min(place => place.at);

        return places.Any(place => holding.Contains(place.slot)) ? HolderStanding.HoldsSlot : HolderStanding.Waiting;
    }

    /// <summary>
    /// The legs holding slots: the front of the line, each fewer legs from the front than the fewest any leg up to it
    /// allows at once - the rule the record's own reading of a place is weighed by.
    /// </summary>
    private static List<SlotEntry> Holding(IReadOnlyList<SlotEntry> line)
    {
        var holding = new List<SlotEntry>();
        var fewest = int.MaxValue;

        foreach (var entry in line)
        {
            fewest = Math.Min(fewest, entry.Slots);

            if (holding.Count >= fewest)
            {
                break;
            }

            holding.Add(entry);
        }

        return holding;
    }

    private static bool RunLockMatches(LockEntry entry, LockRequest request)
        => string.Equals(entry.Host, request.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Tree, request.Tree, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the sentence says of where a holder's leg stands - the endings a dry run's line may take, a closed list
    /// this type owns; nothing where it asked for no slot.
    /// </summary>
    private static string Tail(HolderStanding? standing, int ahead)
        => standing switch
        {
            HolderStanding.Waiting => $", whose leg has not started: it waits for a heavy-leg slot, {ReportText.Counted(ahead, "leg")} ahead of it",
            HolderStanding.HoldsSlot => ", whose leg holds a heavy-leg slot: it runs, or starts as soon as the memory and the room allow",
            HolderStanding.OnHost => ", whose leg asks that host for its slot: whether it runs or waits is recorded there",
            _ => string.Empty,
        };

    private readonly RunLock _runLock = runLock;
    private readonly HeavyLegSlots _slots = slots;
    private readonly IProcessIdentity _identity = identity;

    /// <summary>
    /// The words a sound dry run ends with where a run holds a tree its <c>--apply</c> needs: that the request is not
    /// what stops it, and that waiting is all there is to do.
    /// </summary>
    public const string Blocked = "and nothing in this request needs correcting; --apply is refused now only because a run holds what it needs, named on "
        + "the line(s) 'held now:' above: run the same command again once that run has ended or is stopped";

    /// <summary>
    /// The words a sound dry run of a deletion ends with where a run holds only a copy of the worktree: its
    /// <c>--apply</c> would fold and close the agent and could not finish the removal.
    /// </summary>
    public const string RemovalHeld = "and nothing in this request needs correcting; --apply would fold and close it now and could not finish its removal, "
        + "because a run holds a copy of its worktree, named on the line(s) 'held now:' above: run it once that run has ended or is stopped, or run it now "
        + "and run delete-agent again afterwards";

    /// <summary>What a dry run that is refused for its own paths, rows or consents ends with where a run also holds what its <c>--apply</c> needs.</summary>
    public const string AlsoHeld = "; a run also holds what --apply needs, named on the line(s) 'held now:' above";
}
