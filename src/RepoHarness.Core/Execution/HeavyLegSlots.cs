using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>A heavy leg that asked its machine for a slot: holding one, or waiting its turn.</summary>
/// <param name="Machine">The machine it runs on, by the name it had then, for whoever reads the entry.</param>
/// <param name="ProcessId">The process that asked, which holds the slot until it gives it back or ends.</param>
/// <param name="RunId">The run the leg is part of.</param>
/// <param name="AskedUtc">When it asked, for display; the order is the file's own.</param>
/// <param name="Command">The command running the leg.</param>
/// <param name="Leg">The leg.</param>
/// <param name="Host">The host its tree is on, as the command line names it.</param>
/// <param name="Tree">The tree it works in, on that host.</param>
/// <param name="Slots">How many heavy legs the configuration of the command that asked lets this machine run at once.</param>
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
    string? Variant = null)
{
    /// <summary>The entry as a waiting leg names who holds a slot: its tree, as a lock names it, then the process.</summary>
    public string Describe()
        => $"{RunLock.TreeNamed(Host, Tree, Variant)} (leg '{Leg}', {Command}, {ProcessHolders.Describe(Machine, ProcessId, RunId, AskedUtc)})";
}

/// <summary>A heavy leg's claim on the room of one filesystem of its machine, held while its work runs.</summary>
/// <param name="Holder">The leg, as its slot's entry names it: whose process holds the claim, and gives it back.</param>
/// <param name="Filesystem">The filesystem its build fills, as this machine names it.</param>
/// <param name="Bytes">What its build still needed there as it was admitted.</param>
public sealed record RoomClaim(SlotEntry Holder, string Filesystem, long Bytes);

/// <summary>What a heavy leg's build needs of its machine's room, as its admission asks for it.</summary>
/// <param name="Bytes">What it still needs.</param>
/// <param name="Source">What said how much, as a line says it.</param>
/// <param name="At">
/// A path of this machine on the filesystem the build fills: its build directory, or for a WSL distribution's leg the
/// drive where WSL keeps the distribution's disk.
/// </param>
/// <param name="Where">How a line names that room beyond its filesystem: empty, or <c>, where WSL keeps its disk</c>.</param>
public sealed record AdmissionRoom(long Bytes, string Source, string At, string Where);

/// <summary>Whether a leg's claim on its machine's room fits beside what the other legs there claim, and claimed where it does.</summary>
/// <param name="Fits">Whether it fits, and was claimed; true too where the room could not be read.</param>
/// <param name="Disk">The room as it was read, or <see langword="null"/> where it could not be.</param>
/// <param name="Claimed">What the other legs admitted there claim.</param>
/// <param name="Claimants">The legs claiming it, each as its slot's entry names it.</param>
/// <param name="Unmeasured">Why the room could not be read, where it could not.</param>
internal sealed record RoomStanding(bool Fits, DiskSpace? Disk, long Claimed, IReadOnlyList<SlotEntry> Claimants, string? Unmeasured)
{
    /// <summary>
    /// The room as a line says it of <paramref name="room"/>: <c>'/' has 40 GiB free, ~12 GiB of it claimed by ..., and
    /// this leg needs ~31 GiB, as its buildSpaceGiB, 31, declares</c>.
    /// </summary>
    public string Describe(AdmissionRoom room)
    {
        var disk = Disk ?? throw new InvalidOperationException("A room that was not read says nothing of what it holds.");
        var beside = Claimants.Count == 0
            ? string.Empty
            : $", ~{DiskSpace.Size(Claimed)} of it claimed by {string.Join("; ", Claimants.Select(claimant => claimant.Describe()))}";

        return $"'{disk.Filesystem}'{room.Where} has {DiskSpace.Size(disk.FreeBytes)} free{beside}, and this leg needs "
            + $"~{DiskSpace.Size(room.Bytes)}, {room.Source}";
    }
}

/// <summary>Where a leg stands among the heavy legs asking its machine for a slot.</summary>
/// <param name="Holding">Whether it holds a slot.</param>
/// <param name="Holders">The legs holding the machine's slots, first in line first: itself among them where it holds one.</param>
/// <param name="Ahead">How many legs are ahead of it in line.</param>
/// <param name="Slots">
/// How many heavy legs may run at once while it waits: the fewest any leg up to it in line allows, its own included.
/// </param>
internal sealed record SlotStanding(bool Holding, IReadOnlyList<SlotEntry> Holders, int Ahead, int Slots);

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
/// Each entry carries the count its own command's configuration allows, since commands of repositories that declare
/// different counts share one record: a leg starts only while it is fewer legs from the front than the fewest any leg
/// up to it allows, so the legs holding slots are always the front of the line, no leg ever runs beside more legs than
/// its own configuration allows, and none passes a leg that asked before it. A command whose configuration declares no
/// admission never joins the line.
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
/// Beside the slots, the room each admitted leg's build still needed as it was let start, by the filesystem it fills: a
/// command counts the room its own legs need as it places them, and two commands each placing one leg on one host both
/// found it room and filled its disk between them. Kept in a record of its own beside the slots', which a build from
/// before it never reads: one that found a member it does not know in the slots' would refuse them.
/// </para>
/// </remarks>
/// <param name="fileSystem">Reads and writes the record.</param>
/// <param name="output">Says what was reclaimed, and what could not be given back.</param>
/// <param name="identity">This process, and how the liveness of another is told.</param>
/// <param name="path">Names the record, asked the first time a leg asks for a slot.</param>
public sealed class HeavyLegSlots(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity, Func<string> path)
{
    /// <summary>The command name this reports under.</summary>
    public const string CommandName = "admission";

    private const string Subject = "The record of the heavy legs admitted onto this machine";

    private const string Consequence = "Until it can be, no heavy leg is admitted onto this machine.";

    private const string RoomSubject = "The record of the room the heavy legs admitted onto this machine claim";

    private const string RoomConsequence = "Until it can be, no heavy leg whose build needs room is admitted onto this machine.";

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;
    private readonly IProcessIdentity _identity = identity;
    private readonly Lazy<MachineWideList<SlotEntry>> _record = new(() => Record<SlotEntry>(fileSystem, path, Subject, Consequence));
    private readonly Lazy<MachineWideList<RoomClaim>> _claims = new(() => Record<RoomClaim>(fileSystem, () => RoomPathFor(path()), RoomSubject, RoomConsequence));

    /// <summary>Slots kept in the record at <paramref name="path"/>.</summary>
    /// <param name="fileSystem">Reads and writes the record.</param>
    /// <param name="output">Says what was reclaimed, and what could not be given back.</param>
    /// <param name="identity">This process, and how the liveness of another is told.</param>
    /// <param name="path">The record.</param>
    public HeavyLegSlots(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity, string path)
        : this(fileSystem, output, identity, () => path)
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
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(output);

        var machine = platform.MachineId;

        if (machine.ByName is { } why)
        {
            output.Info(
                CommandName,
                $"This machine's heavy legs are recorded under its name, '{machine.Id}', since {why}; a command started after "
                + "that name changes keeps a record of its own, and admits its heavy legs beside the ones recorded under the old.");
        }

        return UserState.File(FileNameFor(machine.Id));
    }

    /// <summary>The record's file name for the machine <paramref name="machineId"/> tells apart.</summary>
    /// <param name="machineId">What tells the machine from every other.</param>
    public static string FileNameFor(string machineId) => $"admission-{FileNames.SafeFor(machineId)}.json";

    /// <summary>The record of the room admitted legs claim, beside the slots' record at <paramref name="slots"/>.</summary>
    /// <param name="slots">The slots' record.</param>
    public static string RoomPathFor(string slots) => Path.ChangeExtension(slots, ".room.json");

    /// <summary>The record, as a refusal names it for whoever must look at what it holds.</summary>
    /// <exception cref="HarnessException">No directory of this user's own could be named to keep it in.</exception>
    public string Location => File.Path;

    /// <summary>The record of the room admitted legs claim, as a refusal names it.</summary>
    /// <exception cref="HarnessException">No directory of this user's own could be named to keep it in.</exception>
    public string RoomLocation => Claims.Path;

    private MachineWideList<SlotEntry> File => _record.Value;

    private MachineWideList<RoomClaim> Claims => _claims.Value;

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
    /// <exception cref="HarnessException">The record could not be named, read or written.</exception>
    internal SlotPlace Ask(string runId, string command, string leg, string host, string tree, string? variant, int slots)
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

        return new SlotPlace(this, entry);
    }

    /// <summary>
    /// Where <paramref name="place"/> stands now among the legs asking this machine for a slot, reclaiming those whose
    /// process has ended. A place the record lost - a record removed by hand - is asked for again, last in line.
    /// </summary>
    /// <param name="place">The place a leg was given, and has not given back.</param>
    /// <exception cref="ObjectDisposedException">The place was given back: a leg that gave its place back asks again.</exception>
    /// <exception cref="HarnessException">The record could not be read or written.</exception>
    internal SlotStanding Look(SlotPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        ObjectDisposedException.ThrowIf(place.Left, place);

        return File.Update((entries, afterwards) =>
        {
            var live = Live(entries, entry => entry, "a heavy-leg slot", afterwards);
            var line = live.Contains(place.Entry) ? live : [.. live, place.Entry];
            var ahead = line.IndexOf(place.Entry);
            var holders = Holding(line);

            return (
                line.SequenceEqual(entries) ? null : line,
                new SlotStanding(ahead < holders.Count, holders, ahead, line.Take(ahead + 1).Min(entry => entry.Slots)));
        });
    }

    /// <summary>
    /// Claims the room <paramref name="room"/> needs for the leg holding <paramref name="place"/>, where it fits beside what
    /// the other legs admitted onto this machine claim on the same filesystem; where it does not, claims nothing and says
    /// what is there. Read as it is claimed, under the record's lock, so two legs never both claim the one room left.
    /// </summary>
    /// <param name="place">The place of a leg holding a slot, not given back.</param>
    /// <param name="room">What its build needs.</param>
    /// <exception cref="ObjectDisposedException">The place was given back.</exception>
    /// <exception cref="HarnessException">The record could not be named, read or written.</exception>
    /// <remarks>
    /// The other legs' claims are counted whole, though their builds may have written some of it already, which the room
    /// read now shows gone: a leg waits a little longer than it had to, rather than starting into a disk it fills. Where the
    /// room cannot be read at all, the leg is let start without it, saying so, as one placed where it was unmeasured is.
    /// </remarks>
    internal RoomStanding Claim(SlotPlace place, AdmissionRoom room)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(room);
        ObjectDisposedException.ThrowIf(place.Left, place);

        DiskSpace disk;

        try
        {
            disk = _fileSystem.SpaceAt(room.At);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing claimed, so nothing to give back: the leg is let start on its slot and memory alone, saying why.
            return new RoomStanding(true, null, 0, [], ex.Message);
        }

        var standing = Claims.Update((entries, afterwards) =>
        {
            var live = Live(entries, claim => claim.Holder, "a claim on this machine's room", afterwards);
            var others = live
                .Where(claim => claim.Holder != place.Entry && string.Equals(claim.Filesystem, disk.Filesystem, StringComparison.Ordinal))
                .ToList();
            var claimed = others.Sum(claim => claim.Bytes);
            var fits = disk.FreeBytes - claimed >= room.Bytes;

            // A claim this leg made before - a record removed by hand while it waited - is made again, never twice.
            List<RoomClaim> kept = [.. live.Where(claim => claim.Holder != place.Entry)];
            List<RoomClaim> written = fits ? [.. kept, new RoomClaim(place.Entry, disk.Filesystem, room.Bytes)] : kept;

            return (
                written.SequenceEqual(entries) ? null : written,
                new RoomStanding(fits, disk, claimed, [.. others.Select(claim => claim.Holder)], null));
        });

        place.Claimed |= standing.Fits;
        return standing;
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
    /// allows at once.
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

    /// <summary>
    /// The entries still standing, each whose holder's process has ended reclaimed - as <paramref name="what"/> - and said
    /// to be once the record is let go.
    /// </summary>
    /// <remarks>
    /// Told by the process alone: every entry of these records is this machine's, whatever name it carries, so one whose
    /// id and stamp no process here carries has ended.
    /// </remarks>
    private List<T> Live<T>(IReadOnlyList<T> entries, Func<T, SlotEntry> holder, string what, Action<Action> afterwards)
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

            afterwards(() => _output.Info(CommandName, ProcessHolders.Reclaimed(what, held.Describe())));
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

/// <summary>A leg's place among the heavy legs asking its machine for a slot, given back when disposed.</summary>
public sealed class SlotPlace : IDisposable
{
    private readonly HeavyLegSlots _slots;

    internal SlotPlace(HeavyLegSlots slots, SlotEntry entry)
    {
        _slots = slots;
        Entry = entry;
    }

    /// <summary>What the record holds for the leg.</summary>
    public SlotEntry Entry { get; }

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
