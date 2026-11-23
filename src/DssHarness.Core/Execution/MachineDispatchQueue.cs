using System.Globalization;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Execution;

/// <summary>One class of unit the dispatch queue serves, with its capacity, its holders and its waiting units.</summary>
/// <param name="Class">The class's name: <c>heavy</c>, <c>process</c> or <c>agent</c>.</param>
/// <param name="Capacity">How many units of it the machine takes at once.</param>
/// <param name="Holders">The units holding, each with its tenant.</param>
/// <param name="Waiting">The units waiting, each with its tenant, its wait and its cost.</param>
public sealed record DispatchClass(
    string Class,
    int Capacity,
    IReadOnlyList<DispatchUnit> Holders,
    IReadOnlyList<DispatchUnit> Waiting);

/// <summary>One unit of a dispatch class, as the queue says it: who is charged for it and what it costs.</summary>
/// <param name="Tenant">The tenant the unit is charged to.</param>
/// <param name="Weight">The tenant's weight.</param>
/// <param name="Priority">The run's priority.</param>
/// <param name="Cost">The unit's estimated cost, in minutes.</param>
/// <param name="Waited">How long the unit has waited, or null where it holds.</param>
/// <param name="Said">The unit as a line names it: its tree, its leg, its command and its process.</param>
public sealed record DispatchUnit(
    string Tenant,
    double Weight,
    int Priority,
    double Cost,
    TimeSpan? Waited,
    string Said);

/// <summary>
/// The machine's dispatch queue, the admission owner grown fair: one queue per machine in <see cref="HeavyLegSlots"/>'s
/// place - it extends the owner, and does not sit beside it - serving the heavy class the slots always served, ordered
/// by tenant instead of by arrival alone. Each unit is charged to a tenant - a local command's repository,
/// <c>local/&lt;tree&gt;</c>, until environments name a finer one - and a tenant's turn comes when the charge against
/// its share is the least: its share its weight times its run's priority, doubled for each period a unit of its has
/// waited past the aging bound, so no weight setting starves a tenant and a tenant with a burst of short arms never
/// holds a machine against another's single long build. Nothing is pre-empted: a started unit runs to its end, and a
/// WSL sweep - one unit whole, one worker - is charged its whole estimated cost, so its tenant waits that much before
/// its next heavy unit. The record stays the one the slots kept, extended: the crash rule with it, a holder whose
/// process is dead released by whoever looks next.
/// </summary>
/// <remarks>
/// The process and agent classes are configured and shown - <see cref="Queue()"/> says their capacities - and nothing
/// asks them yet: a server queues every unit it runs, and the agent class arrives with its wiring. The classes the
/// queue serves today are ordered by the one rule this owner owns.
/// </remarks>
public sealed class MachineDispatchQueue(
    IFileSystem fileSystem,
    IHarnessOutput output,
    IProcessIdentity identity,
    Func<string> path,
    Func<int?>? machineSlots = null,
    Func<Configuration.MachineSettingsFile>? settings = null) : HeavyLegSlots(fileSystem, output, identity, path, machineSlots)
{
    /// <summary>
    /// The heavy class's holders, chosen fairly: of the units waiting, the one whose tenant is charged the least
    /// against its share goes next, ties by arrival, each holder narrowing the room as the slots' own rule always did -
    /// no unit runs beside more than the fewest any holder or it allows, and the machine's own count, where its settings
    /// file declares one, overrides every entry's.
    /// </summary>
    protected internal override List<SlotEntry> HoldingOf(IReadOnlyList<SlotEntry> line, int? machine, DateTimeOffset now)
    {
        var holding = new List<SlotEntry>();
        var left = new List<SlotEntry>(line);

        while (left.Where(Fits).OrderBy(entry => ChargeOf(entry, holding, now)).ThenBy(entry => entry.AskedUtc).FirstOrDefault() is { } next)
        {
            holding.Add(next);
            left.Remove(next);
        }

        return holding;

        // Whether one more unit fits beside what holds: the machine's own count where its settings file declares one,
        // else the fewest slots any holder or the candidate itself allows - the slots' own rule, kept whole.
        bool Fits(SlotEntry entry)
        {
            if (machine is { } count)
            {
                return holding.Count < count;
            }

            var fewest = holding.Select(held => held.Slots).Append(entry.Slots).Min();

            return holding.Count < fewest;
        }
    }

    /// <summary>
    /// The queue as it stands: each class's capacity, its holders and its waiting units, by tenant, with each waiting
    /// unit's cost and wait. The heavy class from the record the slots kept; the process and agent classes their
    /// configured capacities, asked of nothing yet.
    /// </summary>
    public IReadOnlyList<DispatchClass> Queue()
    {
        var admission = settings?.Invoke()?.Read().Admission;
        var heavy = base.Line();
        var now = DateTimeOffset.UtcNow;

        return
        [
            new DispatchClass(
                "heavy",
                MachineSlots(place: null) ?? (heavy.Count > 0 ? heavy.Max(unit => unit.Entry.Slots) : AdmissionSettings.DefaultHeavyLegs),
                [.. heavy.Where(unit => unit.HoldsSlot).Select(unit => UnitOf(unit.Entry, null))],
                [.. heavy.Where(unit => !unit.HoldsSlot).Select(unit => UnitOf(unit.Entry, now - unit.Entry.AskedUtc))]),
            new DispatchClass("process", admission?.Processes ?? Environment.ProcessorCount, [], []),
            new DispatchClass("agent", admission?.Agents ?? AdmissionSettings.DefaultAgents, [], []),
        ];

        DispatchUnit UnitOf(SlotEntry entry, TimeSpan? waited) => new(
            entry.Tenant,
            entry.Weight,
            entry.Priority,
            entry.Cost,
            waited,
            $"{RunLock.TreeNamed(entry.Host, entry.Tree, entry.Variant)} (leg '{entry.Leg}', {entry.Command}, {ProcessHolders.Describe(entry.ProcessId, entry.RunId, entry.AskedUtc)})");
    }

    /// <summary>A tenant's charge against its share: the cost of what its holders hold, over its weight, its priority and its aging.</summary>
    private static double ChargeOf(SlotEntry entry, List<SlotEntry> holding, DateTimeOffset now)
    {
        var charged = holding.Where(held => string.Equals(held.Tenant, entry.Tenant, StringComparison.Ordinal)).Sum(held => held.Cost);
        var oldest = now - entry.AskedUtc;
        var periods = (int)(oldest.TotalMinutes / Math.Max(0.1, entry.AgeAfterMinutes));

        return charged / (entry.Weight * Math.Max(1, entry.Priority) * Math.Pow(2, periods));
    }
}
