using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

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
/// One run - or sweep - in the way of a dry run's <c>--apply</c>: a tree the apply takes, or a copy of the worktree a
/// deletion removes - the lock's entry for it where the lock names it, where its leg stands, and the one sentence that
/// says it. It is exactly one of two: a holder the run lock names, said on a <c>held now:</c> line; or the holder of a
/// copy kept in the worktree, said on the line its family says of that copy.
/// </summary>
public sealed record TreeHolder
{
    private TreeHolder(bool copy, string path, string host, LockEntry? entry, LogOwner? claim, HolderStanding? standing, int? ahead, string said, bool holdsKeptCopy)
    {
        Copy = copy;
        Path = path;
        Host = host;
        Entry = entry;
        Claim = claim;
        Standing = standing;
        Ahead = ahead;
        Said = said;
        HoldsKeptCopy = holdsKeptCopy;
    }

    /// <summary>
    /// Whether what it holds is a copy of the worktree a deletion removes, which leaves that removal unfinished (exit 21),
    /// rather than a tree the apply takes, which refuses it (exit 13).
    /// </summary>
    public bool Copy { get; }

    /// <summary>The tree or the copy it holds: as the lock's entry spells it, or the copy's family where no entry names it.</summary>
    public string Path { get; }

    /// <summary>The host what it holds is on.</summary>
    public string Host { get; }

    /// <summary>
    /// The lock's entry for what it holds; <see langword="null"/> for a copy kept in the worktree that a claim of its
    /// family's own names the holder of, as a sweep's claim names a mutation worker's.
    /// </summary>
    public LockEntry? Entry { get; }

    /// <summary>
    /// What the claim of its family's own records of it - its machine, its process, its run and when it claimed the copy -
    /// for the holder of a copy kept in the worktree that such a claim names, rather than the lock; <see langword="null"/>
    /// where an entry of the lock names it, and where that claim could not be read.
    /// </summary>
    public LogOwner? Claim { get; }

    /// <summary>
    /// Where its leg stands; <see langword="null"/> for a leg of this machine where this machine's record of heavy legs
    /// could not be read, and where no entry of the lock names it.
    /// </summary>
    public HolderStanding? Standing { get; }

    /// <summary>How many legs are ahead of it in line, where it waits.</summary>
    public int? Ahead { get; }

    /// <summary>
    /// The one sentence of it: as a <c>held now:</c> line and a hold's refusal say it, or, for a copy kept in the
    /// worktree, the line its family says of that copy.
    /// </summary>
    public string Said { get; }

    /// <summary>
    /// Whether it holds a copy kept in the worktree, which the line its family says of the copy names, rather than a
    /// <c>held now:</c> line.
    /// </summary>
    public bool HoldsKeptCopy { get; }

    /// <summary>A holder the run lock names: said on a <c>held now:</c> line, as a hold's refusal says it.</summary>
    /// <param name="copy">Whether what it holds is a copy a deletion removes, rather than a tree the apply takes.</param>
    /// <param name="entry">The lock's entry for what it holds.</param>
    /// <param name="standing">Where its leg stands; <see langword="null"/> for a leg of this machine where this machine's record of heavy legs could not be read.</param>
    /// <param name="ahead">How many legs are ahead of it in line, where it waits.</param>
    /// <param name="said">The one sentence of it.</param>
    public static TreeHolder Named(bool copy, LockEntry entry, HolderStanding? standing, int? ahead, string said)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(said);

        return new(copy, entry.Tree, entry.Host, entry, claim: null, standing, ahead, said, holdsKeptCopy: false);
    }

    /// <summary>
    /// The holder of a copy kept in the worktree, which a deletion removes: said on the line its family says of the copy,
    /// with the lock's entry for it where the lock holds a copy of that family, and otherwise with what the family's claim
    /// on the copy records of it (<see cref="KeptCopyLine.Claim"/>).
    /// </summary>
    /// <param name="line">The line its family says of the copy held.</param>
    /// <param name="entry">The lock's entry for the copy, where the lock names its holder; <see langword="null"/> where a claim does.</param>
    /// <param name="standing">Where its leg stands, where an entry of the lock names it.</param>
    /// <param name="ahead">How many legs are ahead of it in line, where it waits.</param>
    public static TreeHolder OfKeptCopy(KeptCopyLine line, LockEntry? entry, HolderStanding? standing, int? ahead)
    {
        ArgumentNullException.ThrowIfNull(line);

        var path = line.HeldPath ?? throw new ArgumentException("A line that names no copy held names no holder.", nameof(line));

        return new(
            copy: true,
            entry?.Tree ?? path,
            entry?.Host ?? HostId.Local.ToString(),
            entry,
            entry is null ? line.Claim : null,
            standing,
            ahead,
            line.Line,
            holdsKeptCopy: true);
    }
}

/// <summary>What a dry run read of who holds what its <c>--apply</c> takes.</summary>
/// <param name="Holders">
/// Every holder: each the run lock names, in the lock file's order, then each that holds a copy kept in the worktree, in
/// the order its family says them.
/// </param>
/// <param name="Unread">Why the lock file could not be read, where it could not; the holders are none then.</param>
/// <param name="StandingUnread">
/// Why this machine's record of heavy legs could not be read, where a holder's leg is one of this machine's and it could
/// not: each holder is still named, without the words that say where such a leg stands. Nothing where no holder's leg
/// is one of this machine's, as the record is not read then.
/// </param>
/// <param name="KeptCopies">
/// What the families of copies kept in the worktree say removing them would leave, each line in its family's own words
/// and with what it is to the removal, where a deletion's dry run asked them; empty where it asked nobody. A copy a run
/// or a sweep holds that a line names is held by one of <see cref="Holders"/>.
/// </param>
public sealed record TreeHeld(IReadOnlyList<TreeHolder> Holders, string? Unread, string? StandingUnread, IReadOnlyList<KeptCopyLine> KeptCopies)
{
    /// <summary>
    /// Whether a run or a sweep holds a copy the deletion removes: a holder, and nothing else a family says - a directory
    /// no sync made goes with the worktree, and copies that could not be looked for are held by nobody.
    /// </summary>
    public bool HoldsCopies => Holders.Any(holder => holder.Copy);

    /// <summary>Whether a run holds a tree the apply takes, which refuses it.</summary>
    public bool HoldsATree => Holders.Any(holder => !holder.Copy);

    /// <summary>Whether a run or a sweep holds only copies, which a deletion leaves unfinished.</summary>
    public bool HoldsOnlyCopies => HoldsCopies && !HoldsATree;
}

/// <summary>
/// Who holds what a command's <c>--apply</c> takes: the run lock asked with the very requests the apply takes, joined
/// with this machine's record of heavy legs, so each holder is named with where its leg stands. Reads both - the record
/// only where a holder's leg is one of this machine's - writes neither and reclaims nothing.
/// </summary>
/// <param name="runLock">The lock, whose live entries in the way name the holders.</param>
/// <param name="slots">The record of heavy legs, whose line says where a holder's leg stands.</param>
/// <param name="identity">This process, which tells this machine from another and spells the holder.</param>
public sealed class TreeHolders(RunLock runLock, HeavyLegSlots slots, IProcessIdentity identity)
{
    /// <summary>
    /// Who holds what <paramref name="takes"/> and <paramref name="removes"/> name - every live entry in the way of any of
    /// them, each copy marked as the copy it is - and who holds the copies kept in the worktree that
    /// <paramref name="kept"/> names held: every holder with where its leg stands. The lock is read here, once, for all of
    /// them - each entry then told to the requests it is in the way of - so one that cannot be read is said as that for all
    /// of them alike, and every holder is named from the same reading. Nothing is taken, written or reclaimed.
    /// </summary>
    /// <param name="layout">The repository, whose main checkout holds the lock file.</param>
    /// <param name="takes">The trees the apply takes, which a run holding refuses it.</param>
    /// <param name="removes">The copies of the worktree hosts keep, as their removal takes them, which a run holding leaves a deletion unfinished.</param>
    /// <param name="kept">What the families of copies kept in the worktree say removing them would leave; none where a deletion asked nobody.</param>
    /// <returns>What was read; <see cref="TreeHeld.Unread"/> where the lock file could not be read.</returns>
    public TreeHeld Read(HarnessLayout layout, IReadOnlyList<LockRequest> takes, IReadOnlyList<LockRequest> removes, IReadOnlyList<KeptCopyLine> kept)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(takes);
        ArgumentNullException.ThrowIfNull(removes);
        ArgumentNullException.ThrowIfNull(kept);

        try
        {
            // A copy kept in the worktree that a run holds is asked of the lock as its removal asks it, in the one reading
            // that asks for the trees, so its holder is named with its entry; one a claim of its family's own holds - a
            // sweep's, on a mutation worker - is named by that claim alone, on its family's line.
            var held = kept.Where(said => said.HeldPath is not null).ToList();
            var entries = _runLock.Holders(layout, [.. takes, .. removes, .. held.Select(said => said.Asked).OfType<LockRequest>()]);

            return Joined(
                [
                    .. entries
                        .Where(entry => takes.Concat(removes).Any(request => RunLockMatches(entry, request)))
                        .Select(entry => (Entry: entry, Copy: removes.Any(request => RunLockMatches(entry, request)))),
                ],
                [
                    .. held.Select(said => (Line: said, Entries: said.Asked is { } asked
                        ? [.. entries.Where(entry => RunLockMatches(entry, asked))]
                        : (IReadOnlyList<LockEntry>)[])),
                ]) with
            {
                KeptCopies = kept,
            };
        }
        catch (HarnessException ex)
        {
            return new TreeHeld([], ex.Message.TrimEnd('.'), null, kept);
        }
    }

    /// <summary>
    /// Every holder joined with the line: each with where its leg stands and the sentence of it - and each holding a copy
    /// kept in the worktree, said on its family's line, with where its leg stands where the lock names it.
    /// </summary>
    private TreeHeld Joined(
        IReadOnlyList<(LockEntry Entry, bool Copy)> entries,
        IReadOnlyList<(KeptCopyLine Line, IReadOnlyList<LockEntry> Entries)> kept)
    {
        var (line, standingUnread) = LineFor([.. entries.Select(entry => entry.Entry), .. kept.SelectMany(copy => copy.Entries)]);

        return new TreeHeld(
            [
                .. entries.Select(entry => Holder(entry.Entry, line, entry.Copy)),
                .. kept.SelectMany(copy => KeptHolders(copy.Line, copy.Entries, line)),
            ],
            null,
            standingUnread,
            []);
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

        return $"{RunLock.TreeNamed(wanted.Host, wanted.Tree, wanted.Variant)} is held by {Held(holder, Tail(Stands(holder, LineFor([holder]).Line)))}.";
    }

    /// <summary>
    /// This machine's record of heavy legs, in line order, read only where one of <paramref name="entries"/> is a leg of
    /// this machine (<see cref="AsksThisMachine"/>), whose standing it says: <see langword="null"/>, with why, where it
    /// could not be read; <see langword="null"/>, with nothing to say, where none of them is such a leg - each stands
    /// without it then, and a record nobody needed is never said to be unreadable.
    /// </summary>
    private (IReadOnlyList<SlotInLine>? Line, string? Unread) LineFor(IReadOnlyList<LockEntry> entries)
    {
        if (!entries.Any(AsksThisMachine))
        {
            return (null, null);
        }

        try
        {
            return (_slots.Line(), null);
        }
        catch (HarnessException ex)
        {
            return (null, ex.Message.TrimEnd('.'));
        }
    }

    /// <summary>
    /// Whether the leg of <paramref name="entry"/>'s run asks this machine's record of heavy legs for its slot: a leg of
    /// this machine itself, and one in a WSL distribution, which the command that dispatched it asks for here, where it
    /// runs (<see cref="HeavyLegSlots"/>) - every leg but one on an ssh host, which asks that host's own record.
    /// </summary>
    private static bool AsksThisMachine(LockEntry entry)
        => !(HostId.TryParse(entry.Host, out var host) && host.Kind == HostKind.Ssh);

    /// <summary>One holder: the lock's entry, joined with the line, and the sentence of it.</summary>
    private TreeHolder Holder(LockEntry entry, IReadOnlyList<SlotInLine>? line, bool copy)
    {
        var stands = Stands(entry, line);

        return TreeHolder.Named(
            copy,
            entry,
            stands.Standing,
            stands.Ahead,
            $"{RunLock.TreeNamed(entry.Host, entry.Tree, entry.Variant)} is held by {Held(entry, Tail(stands))}.");
    }

    /// <summary>
    /// Who holds the copy kept in the worktree that <paramref name="said"/> names, each said on that line: every entry of
    /// the lock for it, joined with the line; or, where no entry names one, the holder its family's claim named.
    /// </summary>
    private static IEnumerable<TreeHolder> KeptHolders(KeptCopyLine said, IReadOnlyList<LockEntry> entries, IReadOnlyList<SlotInLine>? line)
        => entries.Count == 0
            ? [TreeHolder.OfKeptCopy(said, entry: null, standing: null, ahead: null)]
            : entries.Select(entry =>
            {
                var stands = Stands(entry, line);

                return TreeHolder.OfKeptCopy(said, entry, stands.Standing, stands.Ahead);
            });

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
    /// Where the leg of <paramref name="entry"/>'s run stands, and how many legs are ahead of it where it waits - the one
    /// answer every holder and every refusal is said with: where the lock names an ssh host, that host's own record says,
    /// whatever this machine's holds; otherwise - a leg of this machine (<see cref="AsksThisMachine"/>) - it holds a slot
    /// where any of its places in this machine's line does, it waits where the line holds it further back, and it asked
    /// for no slot where the line holds it nowhere. Nothing for a leg of this machine where this machine's record of heavy
    /// legs could not be read: where that leg stands is not known then, and is never said to be a leg that asked for no
    /// slot.
    /// </summary>
    /// <param name="entry">The lock's entry for what the run holds.</param>
    /// <param name="line">This machine's record of heavy legs, in line order; <see langword="null"/> where it could not be read.</param>
    private static (HolderStanding? Standing, int? Ahead) Stands(LockEntry entry, IReadOnlyList<SlotInLine>? line)
    {
        if (!AsksThisMachine(entry))
        {
            return (HolderStanding.OnHost, null);
        }

        if (line is null)
        {
            return (null, null);
        }

        var places = line
            .Select((slot, at) => (slot, at))
            .Where(place => string.Equals(place.slot.Entry.RunId, entry.Holder.RunId, StringComparison.Ordinal)
                && place.slot.Entry.ProcessId == entry.Holder.ProcessId
                && string.Equals(place.slot.Entry.Host, entry.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(place.slot.Entry.Tree, entry.Tree, StringComparison.OrdinalIgnoreCase)
                && string.Equals(place.slot.Entry.Variant ?? string.Empty, entry.Variant ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (places.Count == 0)
        {
            return (HolderStanding.NoSlot, null);
        }

        return places.Any(place => place.slot.HoldsSlot) ? (HolderStanding.HoldsSlot, null) : (HolderStanding.Waiting, places.Min(place => place.at));
    }

    private static bool RunLockMatches(LockEntry entry, LockRequest request)
        => string.Equals(entry.Host, request.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Tree, request.Tree, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the sentence says of where a holder's leg stands - the endings a dry run's line may take, a closed list
    /// this type owns; nothing where it asked for no slot, or where that is not known.
    /// </summary>
    private static string Tail((HolderStanding? Standing, int? Ahead) stands)
        => stands switch
        {
            (HolderStanding.Waiting, { } ahead) => $", whose leg has not started: it waits for a heavy-leg slot, {ReportText.Counted(ahead, "leg")} ahead of it",
            (HolderStanding.HoldsSlot, _) => ", whose leg holds a heavy-leg slot: it runs, or starts as soon as the memory and the room allow",
            (HolderStanding.OnHost, _) => ", whose leg asks that host for its slot: whether it runs or waits is recorded there",
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
    /// The words a sound dry run of a deletion ends with where a run or a sweep holds only a copy of the worktree: its
    /// <c>--apply</c> would do what it does before the removal - fold and close a live agent, keep a closed one's
    /// evidence again - and could not finish the removal. Each such holder is named above with what holds it: a host's
    /// copy on a <c>held now:</c> line, a copy kept in the worktree on the line its family says of it.
    /// </summary>
    public const string RemovalHeld = "and nothing in this request needs correcting; --apply would go as far as its removal now and could not finish it, "
        + "because a run or a sweep holds a copy of its worktree, named above with what holds it: run it once that has ended or is stopped, or run it now "
        + "and run delete-agent again afterwards";

    /// <summary>What a dry run that is refused for its own paths, rows or consents ends with where a run also holds what its <c>--apply</c> needs.</summary>
    public const string AlsoHeld = "; a run also holds what --apply needs, named on the line(s) 'held now:' above";
}
