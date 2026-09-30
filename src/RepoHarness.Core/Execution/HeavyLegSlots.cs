using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Execution;

/// <summary>A heavy leg that asked its machine for a slot: holding one, or waiting its turn.</summary>
/// <param name="Machine">The machine it runs on.</param>
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
/// One file per user of the machine, among that user's own state, whichever repository or worktree a command runs in:
/// separate commands - worktrees each running a gate of their own - are what share a machine's memory, and no one of
/// them can see the others' legs. A WSL distribution's legs are asked for by the command that dispatched them, on this
/// machine, since they run on it.
/// </para>
/// <para>
/// A slot is held by its process, never by a timeout: an entry whose process on this machine has ended - a command
/// that crashed, or was killed, holding a slot - is reclaimed by whoever looks next, and said to be, as a run lock's
/// is. An entry of another machine's, in a home directory two machines share, is left alone and counts for nothing
/// here.
/// </para>
/// </remarks>
/// <param name="fileSystem">Reads and writes the record.</param>
/// <param name="output">Says what was reclaimed, and what could not be given back.</param>
/// <param name="identity">This process, and how the liveness of another is told.</param>
/// <param name="path">The record.</param>
public sealed class HeavyLegSlots(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity, string path)
{
    /// <summary>The command name this reports under.</summary>
    public const string CommandName = "admission";

    private readonly IHarnessOutput _output = output;
    private readonly IProcessIdentity _identity = identity;

    private readonly MachineWideList<SlotEntry> _file = new(
        fileSystem,
        path,
        "The record of the heavy legs admitted onto this machine",
        "Until it can be, no heavy leg is admitted onto this machine.",
        "Remove it once no heavy leg runs or waits on this machine.");

    /// <summary>Where the record is kept for the user running this process.</summary>
    public static string DefaultPath => UserState.File("admission.json");

    /// <summary>The record, as every refusal names it.</summary>
    public string Location => _file.Path;

    /// <summary>
    /// Records a leg as asking for a slot, last in line, and returns its place; given back when the place is disposed.
    /// </summary>
    /// <param name="runId">The run the leg is part of.</param>
    /// <param name="command">The command running it.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="tree">The tree it works in, as a message names it.</param>
    /// <param name="variant">Its build variant, where it has one.</param>
    /// <exception cref="Results.HarnessException">The record could not be read or written.</exception>
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

        _file.Update(entries => ((IReadOnlyList<SlotEntry>?)[.. Live(entries), entry], 0));

        return new SlotPlace(this, entry);
    }

    /// <summary>
    /// Where <paramref name="place"/> stands now among this machine's <paramref name="slots"/> slots, reclaiming those
    /// whose process has ended. A place the record lost - a record removed by hand - is asked for again, last in line.
    /// </summary>
    /// <param name="place">The place a leg was given.</param>
    /// <param name="slots">How many heavy legs the machine runs at once.</param>
    /// <exception cref="Results.HarnessException">The record could not be read or written.</exception>
    public SlotStanding Look(SlotPlace place, int slots)
    {
        ArgumentNullException.ThrowIfNull(place);

        return _file.Update(entries =>
        {
            var live = Live(entries);
            var kept = live.Contains(place.Entry) ? live : [.. live, place.Entry];
            var line = kept.Where(entry => _identity.IsHere(entry.Machine)).ToList();
            var ahead = line.IndexOf(place.Entry);

            return (kept.SequenceEqual(entries) ? null : kept, new SlotStanding(ahead < slots, [.. line.Take(slots)], ahead));
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
            _file.Update(entries => ((IReadOnlyList<SlotEntry>?)[.. Live(entries).Where(kept => kept != entry)], 0));
        }
        catch (Results.HarnessException ex)
        {
            _output.Warn(CommandName, $"leg '{entry.Leg}' could not give its heavy-leg slot back: {ex.Message} It is reclaimed once this process has ended.");
        }
    }

    /// <summary>The entries still standing, each whose process on this machine has ended reclaimed and said to be.</summary>
    private List<SlotEntry> Live(IReadOnlyList<SlotEntry> entries)
    {
        var kept = new List<SlotEntry>();

        foreach (var entry in entries)
        {
            if (_identity.Stands(entry.Machine, entry.ProcessId, entry.ProcessStamp))
            {
                kept.Add(entry);
                continue;
            }

            _output.Info(CommandName, $"Reclaimed a heavy-leg slot from {entry.Describe()}, which is no longer running.");
        }

        return kept;
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
