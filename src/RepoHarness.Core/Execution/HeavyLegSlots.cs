using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>A heavy leg that asked its machine for a slot: holding one, or waiting its turn.</summary>
/// <param name="Machine">The machine it runs on, by the name it had then, for whoever reads the entry.</param>
/// <param name="ProcessId">The process that asked, which holds the slot for as long as it runs.</param>
/// <param name="ProcessStamp">What tells that process from a later holder of its id; see <see cref="IProcessIdentity"/>.</param>
/// <param name="RunId">The run the leg is part of.</param>
/// <param name="AskedUtc">When it asked, for display; the order is the file's own.</param>
/// <param name="Command">The command running the leg.</param>
/// <param name="Leg">The leg.</param>
/// <param name="Tree">The tree it works in, as a message names it.</param>
/// <param name="Variant">Its build variant, where it has one.</param>
public sealed record SlotEntry(
    string Machine,
    int ProcessId,
    string? ProcessStamp,
    string RunId,
    DateTimeOffset AskedUtc,
    string Command,
    string Leg,
    string Tree,
    string? Variant)
{
    /// <summary>The entry as a waiting leg names who holds a slot: its tree and variant, then the process.</summary>
    public string Describe()
        => $"{Tree}{(Variant is { Length: > 0 } variant ? $" variant '{variant}'" : string.Empty)} (leg '{Leg}', {Command}, "
            + ProcessHolders.Describe(Machine, ProcessId, RunId, AskedUtc) + ")";
}

/// <summary>Where a leg stands among the heavy legs asking its machine for a slot.</summary>
/// <param name="Holding">Whether it holds a slot.</param>
/// <param name="Holders">The legs holding the machine's slots, first in line first: itself among them where it holds one.</param>
/// <param name="Ahead">How many legs are ahead of it in line.</param>
public sealed record SlotStanding(bool Holding, IReadOnlyList<SlotEntry> Holders, int Ahead);

/// <summary>
/// The heavy legs asking this machine for a slot, in the order they asked: a leg holds one of the machine's slots
/// while fewer legs than the machine has slots asked before it, so slots are given in turn, and each is given back
/// when its leg's heavy work ends.
/// </summary>
/// <remarks>
/// <para>
/// One record per machine and per user of it, among that user's own state, whichever repository or worktree a command
/// runs in: separate commands - worktrees each running a gate of their own - are what share a machine's memory, and no
/// one of them can see the others' legs. A WSL distribution's legs are asked for by the command that dispatched them,
/// on this machine, since they run on it.
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
/// was killed, holding a slot - is reclaimed by whoever looks next, and said to be, as a run lock's is.
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

    private readonly IHarnessOutput _output = output;
    private readonly IProcessIdentity _identity = identity;
    private readonly Lazy<MachineWideList<SlotEntry>> _record = new(() => Record(fileSystem, path));

    /// <summary>Slots kept in the record at <paramref name="path"/>.</summary>
    /// <param name="fileSystem">Reads and writes the record.</param>
    /// <param name="output">Says what was reclaimed, and what could not be given back.</param>
    /// <param name="identity">This process, and how the liveness of another is told.</param>
    /// <param name="path">The record.</param>
    public HeavyLegSlots(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity, string path)
        : this(fileSystem, output, identity, () => path)
    {
    }

    /// <summary>The record of <paramref name="platform"/>'s machine, for the user running this process.</summary>
    /// <param name="platform">Tells this machine from every other.</param>
    /// <exception cref="DirectoryNotFoundException">No directory of this user's own could be named.</exception>
    public static string PathFor(IHostPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        return UserState.File(FileNameFor(platform.MachineId));
    }

    /// <summary>The record's file name for the machine <paramref name="machineId"/> tells apart.</summary>
    /// <param name="machineId">What tells the machine from every other.</param>
    public static string FileNameFor(string machineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        return $"admission-{new string([.. machineId.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' ? character : '_')])}.json";
    }

    /// <summary>The record, as every refusal names it.</summary>
    /// <exception cref="HarnessException">No directory of this user's own could be named to keep it in.</exception>
    public string Location => File.Path;

    private MachineWideList<SlotEntry> File => _record.Value;

    /// <summary>
    /// Records a leg as asking for a slot, last in line, and returns its place; given back when the place is disposed.
    /// </summary>
    /// <param name="runId">The run the leg is part of.</param>
    /// <param name="command">The command running it.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="tree">The tree it works in, as a message names it.</param>
    /// <param name="variant">Its build variant, where it has one.</param>
    /// <exception cref="HarnessException">The record could not be named, read or written.</exception>
    public SlotPlace Ask(string runId, string command, string leg, string tree, string? variant)
    {
        var entry = new SlotEntry(
            _identity.CurrentMachine,
            _identity.CurrentId,
            _identity.Current,
            runId,
            DateTimeOffset.UtcNow,
            command,
            leg,
            tree,
            variant);

        File.Update(entries => ((IReadOnlyList<SlotEntry>?)[.. Live(entries), entry], 0));

        return new SlotPlace(this, entry);
    }

    /// <summary>
    /// Where <paramref name="place"/> stands now among this machine's <paramref name="slots"/> slots, reclaiming those
    /// whose process has ended. A place the record lost - a record removed by hand - is asked for again, last in line.
    /// </summary>
    /// <param name="place">The place a leg was given.</param>
    /// <param name="slots">How many heavy legs the machine runs at once.</param>
    /// <exception cref="HarnessException">The record could not be read or written.</exception>
    public SlotStanding Look(SlotPlace place, int slots)
    {
        ArgumentNullException.ThrowIfNull(place);

        return File.Update(entries =>
        {
            var live = Live(entries);
            var line = live.Contains(place.Entry) ? live : [.. live, place.Entry];
            var ahead = line.IndexOf(place.Entry);

            return (line.SequenceEqual(entries) ? null : line, new SlotStanding(ahead < slots, [.. line.Take(slots)], ahead));
        });
    }

    /// <summary>
    /// Gives <paramref name="entry"/>'s slot, or its place in line, back. One that cannot be is said and fails nothing:
    /// the entry names this process, and is reclaimed as a dead holder's is once it has ended.
    /// </summary>
    internal void Leave(SlotEntry entry)
    {
        try
        {
            File.Update(entries => ((IReadOnlyList<SlotEntry>?)[.. Live(entries).Where(kept => kept != entry)], 0));
        }
        catch (HarnessException ex)
        {
            _output.Warn(CommandName, $"leg '{entry.Leg}' could not give its heavy-leg slot back: {ex.Message} It is reclaimed once this process has ended.");
        }
    }

    /// <summary>The entries still standing, each whose process has ended reclaimed and said to be.</summary>
    /// <remarks>
    /// Told by the process alone: every entry of this record is this machine's, whatever name it carries, so one whose
    /// id and stamp no process here carries has ended.
    /// </remarks>
    private List<SlotEntry> Live(IReadOnlyList<SlotEntry> entries)
    {
        var kept = new List<SlotEntry>();

        foreach (var entry in entries)
        {
            if (_identity.IsAlive(entry.ProcessId, entry.ProcessStamp))
            {
                kept.Add(entry);
                continue;
            }

            _output.Info(CommandName, $"Reclaimed a heavy-leg slot from {entry.Describe()}, which is no longer running.");
        }

        return kept;
    }

    /// <summary>The record <paramref name="path"/> names, or a refusal saying it has nowhere to be kept.</summary>
    private static MachineWideList<SlotEntry> Record(IFileSystem fileSystem, Func<string> path)
    {
        string named;

        try
        {
            named = path();
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new HarnessException(HarnessExit.Refused, $"{Subject} has nowhere to be kept: {ex.Message.TrimEnd('.')}. {Consequence}", ex);
        }

        return new(fileSystem, named, Subject, Consequence, "Remove it once no heavy leg runs or waits on this machine.");
    }
}

/// <summary>A leg's place among the heavy legs asking its machine for a slot, given back when disposed.</summary>
public sealed class SlotPlace : IDisposable
{
    private readonly HeavyLegSlots _slots;
    private bool _left;

    internal SlotPlace(HeavyLegSlots slots, SlotEntry entry)
    {
        _slots = slots;
        Entry = entry;
    }

    /// <summary>What the record holds for the leg.</summary>
    public SlotEntry Entry { get; }

    /// <summary>Gives the place back. Calling it twice is not an error; the second call does nothing.</summary>
    public void Dispose()
    {
        if (_left)
        {
            return;
        }

        _left = true;
        _slots.Leave(Entry);
    }
}
