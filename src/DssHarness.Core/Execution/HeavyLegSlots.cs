using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Legs;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Core.Execution;

/// <summary>A heavy leg that asked its machine for a slot: holding one, or waiting its turn.</summary>
/// <param name="Machine">The machine it runs on, by the name it had then, for whoever reads the entry.</param>
/// <param name="ProcessId">The process that asked, which holds the slot until it gives it back or ends.</param>
/// <param name="RunId">The run the leg is part of.</param>
/// <param name="AskedUtc">When it asked, for display; the order is the file's own.</param>
/// <param name="Command">The command running the leg.</param>
/// <param name="Leg">The leg.</param>
/// <param name="Host">The host its tree is on, as the command line names it.</param>
/// <param name="Tree">The tree it works in, on that host.</param>
/// <param name="Slots">
/// How many heavy legs the rule of the command that asked lets this machine run at once: the machine's own count where its
/// settings file declares one, else what that command's config.json declares. Weighed by every reader where the machine
/// declares no count of its own, as the builds before it weigh it; where it does, the machine's count is every leg's.
/// </param>
/// <param name="ProcessStamp">
/// What tells that process from a later holder of its id; see <see cref="IProcessIdentity"/>. Left out where the
/// platform would not say.
/// </param>
/// <param name="Variant">Its build variant, where it has one.</param>
public sealed record SlotEntry(
    string Machine,
    int ProcessId,
    string RunId,
    DateTimeOffset AskedUtc,
    string Command,
    string Leg,
    string Host,
    string Tree,
    int Slots,
    string? ProcessStamp = null,
    string? Variant = null) : IHeldByProcess
{
    /// <summary>The entry as a waiting leg names who holds a slot: its tree, as a lock names it, then the process.</summary>
    public string Describe()
        => $"{RunLock.TreeNamed(Host, Tree, Variant)} (leg '{Leg}', {Command}, {ProcessHolders.Describe(ProcessId, RunId, AskedUtc)})";
}

/// <summary>
/// An entry of one of a machine's heavy-leg records, held by a process of that machine until it gives it back or ends: told
/// standing by that process alone, every entry of those records being the machine's whatever name it carries.
/// </summary>
internal interface IHeldByProcess
{
    /// <summary>The process holding it.</summary>
    int ProcessId { get; }

    /// <summary>What tells that process from a later holder of its id, where the platform said.</summary>
    string? ProcessStamp { get; }

    /// <summary>The entry as a line names its holder.</summary>
    string Describe();
}

/// <summary>
/// A leg making room on one filesystem of its machine from other trees' idle builds, held for as long as it lists and
/// removes them (<see cref="HeavyLegSlots.MakeRoomOn"/>).
/// </summary>
/// <param name="Machine">The machine it runs on, by the name it had then, for whoever reads the entry.</param>
/// <param name="ProcessId">The process making the room, which holds the entry until it gives it back or ends.</param>
/// <param name="RunId">The run the leg is part of.</param>
/// <param name="SinceUtc">When it began making the room, for display.</param>
/// <param name="For">Whom the room is made for, as a line names the leg: <c>leg 'linux' of the main checkout</c>.</param>
/// <param name="Filesystem">The filesystem it makes room on, as this machine names it.</param>
/// <param name="ProcessStamp">What tells that process from a later holder of its id; left out where the platform would not say.</param>
public sealed record RoomMaking(string Machine, int ProcessId, string RunId, DateTimeOffset SinceUtc, string For, string Filesystem, string? ProcessStamp = null)
    : IHeldByProcess
{
    /// <summary>The leg making the room, as a line names it: <c>leg 'linux' of the main checkout (pid 12, run R, since ...)</c>.</summary>
    public string Describe() => $"{For} ({ProcessHolders.Describe(ProcessId, RunId, SinceUtc)})";
}

/// <summary>
/// What asking to make room on a filesystem of this machine came to: the making held, until disposed, or the leg already
/// making it there.
/// </summary>
public sealed class RoomMakingHold : IDisposable
{
    private readonly HeavyLegSlots? _slots;
    private readonly RoomMaking? _entry;
    private bool _left;

    private RoomMakingHold(HeavyLegSlots? slots, RoomMaking? entry, RoomMaking? by)
    {
        _slots = slots;
        _entry = entry;
        By = by;
    }

    /// <summary>The leg already making room there, where one was, and nothing was held; <see langword="null"/> where this one holds it.</summary>
    public RoomMaking? By { get; }

    /// <summary>Whether this leg holds the making of room there.</summary>
    public bool Held => _entry is not null;

    /// <summary>The making held, given back once disposed.</summary>
    internal static RoomMakingHold Taken(HeavyLegSlots slots, RoomMaking entry) => new(slots, entry, null);

    /// <summary>Nothing held, another leg making room there.</summary>
    internal static RoomMakingHold Behind(RoomMaking by) => new(null, null, by);

    /// <summary>Gives the making of room back. Calling it twice is not an error; the second call does nothing.</summary>
    public void Dispose()
    {
        if (_left || _entry is null)
        {
            return;
        }

        _left = true;
        _slots!.StopMaking(_entry);
    }
}

/// <summary>One of the heavy legs in line, and whether it holds a slot: what a dry run reads to say where a holder's leg stands.</summary>
/// <param name="Entry">The leg's entry in the record, in the order it asked.</param>
/// <param name="HoldsSlot">
/// Whether it holds one of the machine's slots - the front of the line, each fewer legs from the front than the fewest
/// any leg up to it allows at once, or than the machine's own count where it declares one, as the record's own reading of
/// a place is weighed by.
/// </param>
public sealed record SlotInLine(SlotEntry Entry, bool HoldsSlot);

/// <summary>A heavy leg's claim on the room of one filesystem of its machine, held while its work runs.</summary>
/// <param name="Holder">The leg, as its slot's entry names it: whose process holds the claim, and gives it back.</param>
/// <param name="Bytes">What its build still needed there as it was placed, claimed whole as it was let start.</param>
/// <param name="Filesystem">
/// The filesystem its build fills, as this machine names it; <see langword="null"/> where its room could not be read as it
/// was let start, which counts against every filesystem of the machine, since nothing says which one it fills. Left out
/// of the record where it is, so read back as it was written.
/// </param>
public sealed record RoomClaim(SlotEntry Holder, long Bytes, string? Filesystem = null);

/// <summary>Whether a leg's claim on its machine's room was taken, and what was there.</summary>
/// <param name="Fits">
/// Whether the leg may start: its need fitted and was claimed, or its room could not be read and was claimed against
/// every filesystem of the machine.
/// </param>
/// <param name="Disk">The room as it was read, or <see langword="null"/> where it could not be.</param>
/// <param name="Claimed">What the other legs admitted onto that room claim.</param>
/// <param name="Claimants">The legs claiming it, each as its slot's entry names it.</param>
/// <param name="Unmeasured">Why the room could not be read, where it could not.</param>
internal sealed record RoomStanding(bool Fits, DiskSpace? Disk, long Claimed, IReadOnlyList<SlotEntry> Claimants, string? Unmeasured)
{
    /// <summary>
    /// The room as a line says it of <paramref name="room"/>: <c>this leg needs ~31 GiB, as its buildSpaceGiB, 31,
    /// declares; 40 GiB free on '/', beside ~12 GiB claimed by ...</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The room was not read.</exception>
    public string Describe(RoomNeed room)
    {
        ArgumentNullException.ThrowIfNull(room);

        var disk = Disk ?? throw new InvalidOperationException("A room that was not read says nothing of what it holds.");
        var beside = Claimants.Count == 0
            ? string.Empty
            : $", beside ~{DiskSpace.Size(Claimed)} claimed by {string.Join("; ", Claimants.Select(claimant => claimant.Describe()))}";

        return disk.Against(DiskSpace.Needs("this leg", room.Bytes, room.Source), room.Where, beside);
    }
}

/// <summary>Where a leg stands among the heavy legs asking its machine for a slot.</summary>
/// <param name="Holding">Whether it holds a slot.</param>
/// <param name="Holders">The legs holding the machine's slots, first in line first: itself among them where it holds one.</param>
/// <param name="InLineAhead">The legs ahead of it in line, first first: those holding slots, then those waiting.</param>
/// <param name="Slots">
/// How many heavy legs may run at once while it waits: the machine's own count where its settings file declares one, else
/// the fewest any leg up to it in line allows, its own included.
/// </param>
internal sealed record SlotStanding(bool Holding, IReadOnlyList<SlotEntry> Holders, IReadOnlyList<SlotEntry> InLineAhead, int Slots)
{
    /// <summary>How many legs are ahead of it in line.</summary>
    public int Ahead => InLineAhead.Count;
}

/// <summary>
/// The heavy legs asking this machine for a slot, in the order they asked: each leg holds one while it is fewer legs from
/// the front of the line than every leg up to it allows at once, so slots are given in turn, and each is given back when
/// its leg's work ends.
/// </summary>
/// <remarks>
/// <para>
/// One record per machine and per user of it, among that user's own state, whichever repository or worktree a command
/// runs in: separate commands - worktrees each running a gate of their own - are what share a machine's memory, and no
/// one of them can see the others' legs. A WSL distribution's legs are asked for by the command that dispatched them,
/// on this machine, since they run on it. A command typed inside a distribution is that distribution's own: it keeps
/// a record there, and cannot see the legs commands typed on Windows run.
/// </para>
/// <para>
/// Each entry carries the count its own command's rule allows, since commands of repositories that declare different
/// counts share one record: a leg starts only while it is fewer legs from the front than the fewest any leg up to it
/// allows, so the legs holding slots are always the front of the line, no leg ever runs beside more legs than its own
/// configuration allows, and none passes a leg that asked before it. A command whose rule declares no admission never
/// joins the line. Where the machine's settings file declares its own count (<see cref="Configuration.MachineSettings"/>),
/// that count is every leg's, read again at each look: what a machine can hold is the machine's, and a worktree whose
/// config.json was committed before the machine's count was raised would else hold every leg behind it to the old one.
/// A file that cannot be read at a look refuses a leg's own admission, before its work; a unit of a leg whose work has
/// begun - an arm of a sweep - is weighed instead by the count the file declared as that work began, which its place
/// carries (<see cref="SlotPlace.WorkBegunBy"/>), and the refusal said once: a file put wrong mid-sweep would else end a
/// leg that has run with a configuration's error. The entries keep the members a build before this one reads - one that
/// found a member it does not know would refuse the record - and such a build still weighs each entry's own count.
/// </para>
/// <para>
/// Named by what tells this machine from every other, never by its name, which a Mac takes from each network it joins:
/// read by name, the entries of the commands still running under the old one would be another machine's to every
/// command started under the new. And one per machine, so a home two machines share holds a record for each: the lock
/// a change is made under holds on one machine, and a record both changed would lose the entries one of them wrote.
/// So every entry is this machine's, whatever name it carries.
/// </para>
/// <para>
/// A slot is held by its process, never by a timeout: an entry whose process has ended - a command that crashed, or
/// was killed, holding a slot - is reclaimed by whoever looks next, and said to be. Told by the process alone, unlike
/// the run lock's entries, which may name another machine.
/// </para>
/// <para>
/// Beside the slots, the room each admitted leg's build still needed as it was placed, by the filesystem it fills, claimed
/// whole as it was let start and held, like its slot, until its work ends: a command counts the room its own legs need as
/// it places them, and two commands each placing one leg on one host both found it room and filled its disk between them.
/// Kept in a record of its own beside the slots', which a build from before it never reads: one that found a member it
/// does not know in the slots' record would refuse that record.
/// </para>
/// <para>
/// And, in a third record beside them, the leg making room on each filesystem from other trees' idle builds, held while it
/// lists and removes them and, like a slot, by its process: one leg at a time on a filesystem
/// (<see cref="MakeRoomOn"/>), so the next reads the room the one before made.
/// </para>
/// </remarks>
/// <param name="fileSystem">Reads and writes the record.</param>
/// <param name="output">Says what was reclaimed, and what could not be given back.</param>
/// <param name="identity">This process, and how the liveness of another is told.</param>
/// <param name="path">Names the slots' record, asked the first time a leg asks for a slot; the room's is named beside it.</param>
/// <param name="machineSlots">
/// The count of heavy legs this machine's settings file declares, read at each look, or <see langword="null"/> where it
/// declares none; none is read where left out. Refused with the file's <see cref="Configuration.ConfigException"/> where
/// the file cannot be read.
/// </param>
public sealed class HeavyLegSlots(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity, Func<string> path, Func<int?>? machineSlots = null)
{
    /// <summary>The command name this reports under.</summary>
    public const string CommandName = "admission";

    private const string Subject = "The record of the heavy legs admitted onto this machine";

    private const string Consequence = "Until it can be, no heavy leg is admitted onto this machine.";

    private const string RoomSubject = "The record of the room the heavy legs admitted onto this machine claim";

    private const string RoomConsequence = "Until it can be, no heavy leg whose build's need is known is admitted onto this machine.";

    private const string MakingSubject = "The record of the legs making room on this machine from other trees' idle builds";

    private const string MakingConsequence = "Until it can be, no leg makes room on this machine from other trees' idle builds.";

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;
    private readonly IProcessIdentity _identity = identity;
    private readonly Func<int?> _machineSlots = machineSlots ?? (() => null);
    private readonly Lazy<MachineWideList<SlotEntry>> _record = new(() => Record<SlotEntry>(fileSystem, path, Subject, Consequence));

    // The settings file's refusal last said at a look that weighed a unit by the count its leg's work began by: said once,
    // whichever unit looks and however often, until the file reads again or is refused in other words.
    private string? _unreadSaid;

    // Named beside the slots' record as that was named, never by naming it again: where the machine is told only by its
    // name, naming it again says so a second time.
    private Lazy<MachineWideList<RoomClaim>>? _claims;
    private Lazy<MachineWideList<RoomMaking>>? _making;

    /// <summary>Slots kept in the record at <paramref name="path"/>.</summary>
    /// <param name="fileSystem">Reads and writes the record.</param>
    /// <param name="output">Says what was reclaimed, and what could not be given back.</param>
    /// <param name="identity">This process, and how the liveness of another is told.</param>
    /// <param name="path">The record.</param>
    /// <param name="machineSlots">The count this machine's settings file declares, read at each look; none where left out.</param>
    public HeavyLegSlots(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity, string path, Func<int?>? machineSlots = null)
        : this(fileSystem, output, identity, () => path, machineSlots)
    {
    }


    /// <summary>
    /// The record of <paramref name="platform"/>'s machine, for the user running this process. Where the machine can be
    /// told only by its name, that is said, with why: a command started after the name changes keeps a record of its own.
    /// </summary>
    /// <param name="platform">Tells this machine from every other.</param>
    /// <param name="output">Says the record is named by the machine's name, where it is.</param>
    /// <exception cref="DirectoryNotFoundException">No directory of this user's own could be named.</exception>
    public static string PathFor(IHostPlatform platform, IHarnessOutput output)
        => UserState.MachineFile(platform, output, CommandName, FilePrefix, "heavy legs", "admits its heavy legs beside the ones recorded under the old");

    /// <summary>The record's file name for the machine <paramref name="machineId"/> tells apart.</summary>
    /// <param name="machineId">What tells the machine from every other.</param>
    public static string FileNameFor(string machineId) => UserState.MachineFileName(FilePrefix, machineId);

    /// <summary>What the record's file name begins with.</summary>
    private const string FilePrefix = "admission";

    /// <summary>The record of the room admitted legs claim, beside the slots' record at <paramref name="slots"/>.</summary>
    /// <param name="slots">The slots' record.</param>
    public static string RoomPathFor(string slots) => Path.ChangeExtension(slots, ".room.json");

    /// <summary>
    /// The record of the legs making room on this machine from other trees' idle builds, beside the slots' record at
    /// <paramref name="slots"/>: one of its own, which a build before it never reads.
    /// </summary>
    /// <param name="slots">The slots' record.</param>
    public static string MakingPathFor(string slots) => Path.ChangeExtension(slots, ".making.json");

    /// <summary>The slots' record, as a refusal names it for whoever must look at what it holds.</summary>
    /// <exception cref="HarnessException">No directory of this user's own could be named to keep it in.</exception>
    public string Location => File.Path;

    /// <summary>The record of the room admitted legs claim, beside the slots', as a refusal names it.</summary>
    /// <exception cref="HarnessException">No directory of this user's own could be named to keep it in.</exception>
    public string RoomLocation => Claims.Path;

    private MachineWideList<SlotEntry> File => _record.Value;

    private MachineWideList<RoomClaim> Claims
        => (_claims ??= new(() => Record<RoomClaim>(_fileSystem, () => RoomPathFor(File.Path), RoomSubject, RoomConsequence))).Value;

    private MachineWideList<RoomMaking> Making
        => (_making ??= new(() => Record<RoomMaking>(_fileSystem, () => MakingPathFor(File.Path), MakingSubject, MakingConsequence))).Value;

    /// <summary>
    /// Holds the making of room on <paramref name="filesystem"/> from other trees' idle builds for <paramref name="forLeg"/>,
    /// where no other leg of this machine makes room there now; where one does, holds nothing and names it. One leg at a
    /// time on a filesystem, across every command this user runs on the machine: two legs short of room there would each
    /// read the room before the other's removals gave it back, each remove what it was short of, and meet in one build.
    /// So the next leg reads the room once the one before has made it, and makes only what it still lacks.
    /// </summary>
    /// <param name="runId">The run the leg is part of.</param>
    /// <param name="forLeg">The leg, as a line names it: <c>leg 'linux' of the main checkout</c>.</param>
    /// <param name="filesystem">The filesystem, as this machine names it.</param>
    /// <exception cref="HarnessException">The record could not be named, read or written.</exception>
    internal RoomMakingHold MakeRoomOn(string runId, string forLeg, string filesystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(forLeg);
        ArgumentException.ThrowIfNullOrWhiteSpace(filesystem);

        var entry = new RoomMaking(_identity.CurrentMachine, _identity.CurrentId, runId, DateTimeOffset.UtcNow, forLeg, filesystem, _identity.Current);

        var by = Making.Update<RoomMaking?>((entries, afterwards) =>
        {
            var live = Live(entries, making => making, "the making of room on this machine", afterwards);

            // Another leg's making of room on the same filesystem, this process's own other legs among them: nothing written
            // then, beyond what was reclaimed.
            if (live.FirstOrDefault(making => string.Equals(making.Filesystem, filesystem, StringComparison.Ordinal)) is { } other)
            {
                return (live.SequenceEqual(entries) ? null : live, other);
            }

            return ([.. live, entry], null);
        });

        return by is null ? RoomMakingHold.Taken(this, entry) : RoomMakingHold.Behind(by);
    }

    /// <summary>
    /// Gives a leg's making of room back. One that cannot be is said and fails nothing: it leaves the record with the next
    /// change this process makes to it, or is reclaimed as a dead holder's is once this process has ended.
    /// </summary>
    internal void StopMaking(RoomMaking entry)
        => Making.GiveBack(
            kept => kept == entry,
            ex => _output.Warn(
                CommandName,
                $"{entry.For} could not give its making of room back: {ex.Message} This process's other legs no longer count it; it "
                + "leaves the record with the next change this process makes to it, or once this process has ended."));

    /// <summary>
    /// The count of heavy legs this machine's settings file declares, read now, or <see langword="null"/> where it declares
    /// none - a file that cannot be read refused as every reader of this record handles a refusal: as the record's own
    /// could not be read, with the settings file's words, and the exit of a configuration that is wrong. But for a look at
    /// <paramref name="place"/>, where it is a unit of a leg whose work has begun (<see cref="SlotPlace.WorkBegunBy"/>):
    /// that unit is weighed by the count the file declared as that work began, and the refusal said once, until the file
    /// reads again - refused, a file put wrong mid-sweep would end a leg that has run with a configuration's error, losing
    /// its line and its verdict.
    /// </summary>
    /// <param name="place">The place looked at; <see langword="null"/> for a reading of the whole line.</param>
    /// <exception cref="HarnessException">This machine's settings file could not be read, and no leg's work began by it.</exception>
    private int? MachineSlots(SlotPlace? place)
    {
        try
        {
            var read = _machineSlots();

            Volatile.Write(ref _unreadSaid, null);
            return read;
        }
        catch (Configuration.ConfigException ex) when (place?.WorkBegunBy is { } begun)
        {
            if (!string.Equals(Interlocked.Exchange(ref _unreadSaid, ex.Message), ex.Message, StringComparison.Ordinal))
            {
                _output.Warn(
                    CommandName,
                    $"{ex.Message} The units of a leg whose work had begun - the arms of a sweep - are still admitted, each by the "
                    + "count the file declared as that work began.");
            }

            return begun.SettingsFile?.HeavyLegs;
        }
        catch (Configuration.ConfigException ex)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, ex.Message, ex);
        }
    }

    /// <summary>
    /// Records a leg as asking for a slot, last in line, and returns its place; given back when the place is disposed.
    /// </summary>
    /// <param name="runId">The run the leg is part of.</param>
    /// <param name="command">The command running it.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="host">The host its tree is on, as the command line names it.</param>
    /// <param name="tree">The tree it works in, on that host.</param>
    /// <param name="variant">Its build variant, where it has one.</param>
    /// <param name="slots">How many heavy legs the command's configuration lets this machine run at once.</param>
    /// <param name="workBegunBy">
    /// The reading of the machine's rule the leg's work began by, where what asks is a unit of that work; see
    /// <see cref="SlotPlace.WorkBegunBy"/>.
    /// </param>
    /// <exception cref="HarnessException">The record could not be named, read or written.</exception>
    internal SlotPlace Ask(string runId, string command, string leg, string host, string tree, string? variant, int slots, AdmissionReading? workBegunBy = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);

        var entry = new SlotEntry(
            _identity.CurrentMachine,
            _identity.CurrentId,
            runId,
            DateTimeOffset.UtcNow,
            command,
            leg,
            host,
            tree,
            slots,
            _identity.Current,
            variant);

        File.Change((entries, afterwards) => [.. Live(entries, entry => entry, "a heavy-leg slot", afterwards), entry]);

        return new SlotPlace(this, entry, workBegunBy);
    }

    /// <summary>
    /// Where <paramref name="place"/> stands now among the legs asking this machine for a slot, reclaiming those whose
    /// process has ended. A place the record lost - a record removed by hand - is asked for again, last in line.
    /// </summary>
    /// <param name="place">The place a leg was given, and has not given back.</param>
    /// <exception cref="ObjectDisposedException">The place was given back: a leg that gave its place back asks again.</exception>
    /// <exception cref="HarnessException">
    /// The record could not be read or written, or this machine's settings file could not be read for a leg's own admission.
    /// </exception>
    internal SlotStanding Look(SlotPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        ObjectDisposedException.ThrowIf(place.Left, place);

        // Read before the record is taken, and at each look: a machine whose count is raised while legs wait takes them by it.
        var machine = MachineSlots(place);

        return File.Update((entries, afterwards) =>
        {
            var live = Live(entries, entry => entry, "a heavy-leg slot", afterwards);
            var line = live.Contains(place.Entry) ? live : [.. live, place.Entry];
            var ahead = line.IndexOf(place.Entry);
            var holders = Holding(line, machine);

            return (
                line.SequenceEqual(entries) ? null : line,
                new SlotStanding(ahead < holders.Count, holders, [.. line.Take(ahead)], machine ?? line.Take(ahead + 1).Min(entry => entry.Slots)));
        });
    }

    /// <summary>
    /// Claims the room <paramref name="room"/> needs for the leg holding <paramref name="place"/>, where it fits beside what
    /// the other legs admitted onto this machine claim on the same filesystem; where it does not, claims nothing, and
    /// returns what is there. The other claims are read, and this one written, under the room record's lock, so two legs
    /// never both claim the one room left; the free room is read just before.
    /// </summary>
    /// <param name="place">The place of a leg holding a slot, not given back.</param>
    /// <param name="room">What its build needs.</param>
    /// <param name="takeUnread">
    /// Whether a room that cannot be read lets the leg start: true where this wait has read nothing of it, so a machine
    /// whose room never reads still takes the leg; false where it read the room before, and decides nothing on a reading
    /// that old.
    /// </param>
    /// <exception cref="ObjectDisposedException">The place was given back.</exception>
    /// <exception cref="HarnessException">The record could not be named, read or written.</exception>
    /// <remarks>
    /// The other legs' claims are counted whole, though their builds may have written some of it already, which the room
    /// read now shows gone, and they are held until those legs' work ends, tests included: a leg can wait for that, and
    /// be not-admitted where that is longer than its machine allows, rather than start into a disk it fills. A room that
    /// cannot be read, taken, is claimed against every filesystem of the machine, since nothing says which one it fills.
    /// </remarks>
    internal RoomStanding Claim(SlotPlace place, RoomNeed room, bool takeUnread)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(room);
        ObjectDisposedException.ThrowIf(place.Left, place);

        var (disk, unmeasured) = room.At is { } at
            ? DiskSpace.Measure(_fileSystem, at)
            : (null, room.Unmeasured ?? "it was not measured as the leg was placed");

        return Claims.Update((entries, afterwards) =>
        {
            var live = Live(entries, claim => claim.Holder, "a claim on this machine's room", afterwards);

            // At most one claim per leg: one of its own already there is replaced, never added to.
            List<RoomClaim> kept = [.. live.Where(claim => claim.Holder != place.Entry)];

            if (disk is null)
            {
                if (!takeUnread)
                {
                    return (kept.SequenceEqual(entries) ? null : kept, new RoomStanding(false, null, 0, [], unmeasured));
                }

                // Set as the claim is decided, so a line said after the record is let go that fails never leaves a
                // claim this process does not give back.
                place.Claimed = true;

                return (
                    [.. kept, new RoomClaim(place.Entry, room.Bytes)],
                    new RoomStanding(true, null, 0, [], unmeasured));
            }

            var others = kept
                .Where(claim => claim.Filesystem is null || string.Equals(claim.Filesystem, disk.Filesystem, StringComparison.Ordinal))
                .ToList();
            var claimed = others.Sum(claim => claim.Bytes);
            var fits = disk.FreeBytes - claimed >= room.Bytes;

            List<RoomClaim> written = fits ? [.. kept, new RoomClaim(place.Entry, room.Bytes, disk.Filesystem)] : kept;
            place.Claimed |= fits;

            return (
                written.SequenceEqual(entries) ? null : written,
                new RoomStanding(fits, disk, claimed, [.. others.Select(claim => claim.Holder)], null));
        });
    }

    /// <summary>
    /// Gives <paramref name="entry"/>'s slot, or its place in line, back, and the room it claimed where it claimed any.
    /// One that cannot be is said and fails nothing: this process's other legs no longer count it, and it leaves the
    /// record with the next change this process makes to it, or is reclaimed as a dead holder's is once this process has
    /// ended.
    /// </summary>
    internal void Leave(SlotEntry entry, bool claimed)
    {
        File.GiveBack(kept => kept == entry, ex => CouldNotGiveBack(entry, "its heavy-leg slot", ex));

        if (claimed)
        {
            Claims.GiveBack(kept => kept.Holder == entry, ex => CouldNotGiveBack(entry, "the room it claimed", ex));
        }
    }

    private void CouldNotGiveBack(SlotEntry entry, string what, HarnessException ex)
        => _output.Warn(
            CommandName,
            $"leg '{entry.Leg}' could not give {what} back: {ex.Message} This process's other legs no longer count it; it "
            + "leaves the record with the next change this process makes to it, or once this process has ended.");

    /// <summary>
    /// The legs holding slots: the front of the line, each fewer legs from the front than the fewest any leg up to it
    /// allows at once - or, where the machine declares its own count (<paramref name="machine"/>), than that count, whatever
    /// any entry's command allowed.
    /// </summary>
    private static List<SlotEntry> Holding(IReadOnlyList<SlotEntry> line, int? machine)
    {
        var holding = new List<SlotEntry>();
        var fewest = int.MaxValue;

        foreach (var entry in line)
        {
            fewest = machine ?? Math.Min(fewest, entry.Slots);

            if (holding.Count >= fewest)
            {
                break;
            }

            holding.Add(entry);
        }

        return holding;
    }

    /// <summary>
    /// The entries still standing in the order they asked, first in line first, each with whether it holds a slot - the
    /// front of the line, as the record's own reading of a place is weighed by - with nothing reclaimed and nothing
    /// written: what a dry run reads to say where a holder's leg stands, and where in the line it is. An entry whose
    /// process has ended is passed over without a word, and stays until a leg asks or looks again.
    /// </summary>
    /// <exception cref="HarnessException">The record could not be named, read or written, or this machine's settings file could not be read.</exception>
    public IReadOnlyList<SlotInLine> Line()
    {
        var machine = MachineSlots(place: null);

        return File.Update<IReadOnlyList<SlotInLine>>((entries, _) =>
        {
            var live = Live(entries, entry => entry, "a heavy-leg slot", afterwards: null);
            var holding = new HashSet<SlotEntry>(Holding(live, machine));

            return ((IReadOnlyList<SlotEntry>?)null, (IReadOnlyList<SlotInLine>)[.. live.Select(entry => new SlotInLine(entry, holding.Contains(entry)))]);
        });
    }

    /// <summary>
    /// The entries still standing, each whose holder's process has ended reclaimed - as <paramref name="what"/> - and said
    /// to be once the record is let go, where <paramref name="afterwards"/> is given: a reading that reclaims nothing and
    /// writes nothing passes none, and a dead holder's entry is passed over without a word.
    /// </summary>
    /// <remarks>
    /// Told by the process alone: every entry of these records is this machine's, whatever name it carries, so one whose
    /// id and stamp no process here carries has ended.
    /// </remarks>
    private List<T> Live<T>(IReadOnlyList<T> entries, Func<T, IHeldByProcess> holder, string what, Action<Action>? afterwards)
    {
        var kept = new List<T>();

        foreach (var entry in entries)
        {
            var held = holder(entry);

            if (_identity.IsAlive(held.ProcessId, held.ProcessStamp))
            {
                kept.Add(entry);
                continue;
            }

            afterwards?.Invoke(() => _output.Info(CommandName, ProcessHolders.Reclaimed(what, held.Describe())));
        }

        return kept;
    }

    /// <summary>The record <paramref name="path"/> names, or a refusal saying it has nowhere to be kept.</summary>
    private static MachineWideList<T> Record<T>(IFileSystem fileSystem, Func<string> path, string subject, string consequence)
    {
        string named;

        try
        {
            named = path();
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new HarnessException(HarnessExit.Refused, $"{subject} has nowhere to be kept: {ex.Message.TrimEnd('.')}. {consequence}", ex);
        }

        return new(fileSystem, named, subject, consequence, "Remove it once no heavy leg runs or waits on this machine.");
    }
}

/// <summary>A leg's place among the heavy legs asking its machine for a slot, given back - with any room it claimed - when disposed.</summary>
public sealed class SlotPlace : IDisposable
{
    private readonly HeavyLegSlots _slots;

    internal SlotPlace(HeavyLegSlots slots, SlotEntry entry, AdmissionReading? workBegunBy = null)
    {
        _slots = slots;
        Entry = entry;
        WorkBegunBy = workBegunBy;
    }

    /// <summary>What the record holds for the leg.</summary>
    public SlotEntry Entry { get; }

    /// <summary>
    /// The reading of the machine's rule its leg's work began by (<see cref="Runs.PlacedLeg.AdmittedBy"/>), where the place
    /// is a unit's of that work - an arm of a sweep: a look that cannot read the machine's settings file weighs it by the
    /// count the file declared then (<see cref="AdmissionReading.SettingsFile"/>), or by each entry's own where it declared
    /// none. <see langword="null"/> for a leg's own place, asked before its work, which such a file refuses.
    /// </summary>
    internal AdmissionReading? WorkBegunBy { get; }

    /// <summary>Whether the place was given back, after which it is never looked at, nor held, again.</summary>
    internal bool Left { get; private set; }

    /// <summary>Whether the leg claimed room on its machine, which is given back with the place.</summary>
    internal bool Claimed { get; set; }

    /// <summary>Gives the place back. Calling it twice is not an error; the second call does nothing.</summary>
    public void Dispose()
    {
        if (Left)
        {
            return;
        }

        Left = true;
        _slots.Leave(Entry, Claimed);
    }
}
