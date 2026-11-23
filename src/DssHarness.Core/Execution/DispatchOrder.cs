namespace DssHarness.Core.Execution;

/// <summary>
/// What the dispatch queue keeps of one unit beside its entry in the slots' record (<see cref="MachineDispatchQueue.QueuePathFor"/>):
/// whom it is charged to, what multiplies its tenant's share, what it is expected to cost, and since when it holds a slot.
/// </summary>
/// <param name="Holder">The unit's entry in the slots' record, which the row is of: a row whose entry has gone counts for nothing.</param>
/// <param name="Tenant">The tenant it is charged to (<see cref="MachineDispatchQueue.TenantOf"/>).</param>
/// <param name="Priority">Its run's priority, as its workflow declared it; at least 1.</param>
/// <param name="Cost">
/// What it is expected to cost, in minutes: the median of the newest durations its name's history holds
/// (<see cref="UnitDurations.CostOf"/>), or one quantum where it holds none.
/// </param>
/// <param name="AgeAfterMinutes">How many minutes it waits before its tenant's share begins to double, as its rule said.</param>
public sealed record QueuedUnit(SlotEntry Holder, string Tenant, int Priority, double Cost, double AgeAfterMinutes)
{
    /// <summary>Its tenant's weight: 1 for every tenant of a local command (<see cref="DispatchOrder.LocalWeight"/>).</summary>
    public double Weight { get; init; } = DispatchOrder.LocalWeight;

    /// <summary>
    /// When the queue granted it a slot, written in the same change of the record that granted it: the unit holds its slot
    /// from then until it gives it back, whatever arrives after it. <see langword="null"/> while it waits.
    /// </summary>
    public DateTimeOffset? HeldSinceUtc { get; init; }
}

/// <summary>
/// A waiting tenant's deficit in the queue's round-robin (<see cref="DispatchOrder"/>): the credit its units' costs are
/// weighed against, kept while it has a unit waiting and reset to nothing once it has none.
/// </summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Deficit">Its credit, in minutes of cost; below nothing where a unit started on a credit it had not earned.</param>
public sealed record TenantDeficit(string Tenant, double Deficit);

/// <summary>What the dispatch queue keeps beside the slots' record: a row of each unit, and each waiting tenant's deficit.</summary>
/// <param name="Units">A row of each unit this build asked, in the order they asked.</param>
/// <param name="Tenants">Each tenant with a unit waiting that has a deficit kept.</param>
public sealed record DispatchRecord(IReadOnlyList<QueuedUnit> Units, IReadOnlyList<TenantDeficit> Tenants)
{
    /// <summary>The record where no unit has asked.</summary>
    public static DispatchRecord Empty { get; } = new([], []);
}

/// <summary>
/// The dispatch queue's one order, and the slots it grants: deficit round-robin over the tenants of the units waiting, as
/// plan 2.0 part 06 sets it. Each round, every tenant with a unit waiting is credited its share - its unit's weight times
/// its run's priority, doubled for each whole period of the unit's aging bound it has waited - and a unit is served once
/// its tenant's deficit covers its estimated cost, which is then debited: so a tenant with a burst of short units and a
/// tenant with one long unit each get the machine in proportion, and a long unit - a WSL sweep, sent whole - leaves its
/// tenant that much behind before its next. The rounds are counted, never stepped: a tenant's turn comes after as many
/// rounds as its deficit is short of its unit's cost, over its share, so a decision is linear in the units waiting
/// whatever their costs. Ties go to the unit that asked first; within a tenant, its units are served in the order they
/// asked. A tenant's deficit is kept while it has a unit waiting, and reset to nothing once it has none.
/// </summary>
/// <remarks>
/// <para>
/// A slot is granted by a mark written in the same change of the record (<see cref="QueuedUnit.HeldSinceUtc"/>): the marked
/// units hold, until they give their slots back, and the order chooses only among the units not marked. The slots' own
/// rules stay whole: a unit is granted one only where the holders are fewer than the machine's own count, where its
/// settings file declares one, else than the fewest any holder or the unit itself allows. The unit the order puts first
/// that does not fit holds back every unit behind it, so a unit whose own rule allows fewer legs at once is never passed
/// for ever: the holders end, none is granted beside them meanwhile, and it fits.
/// </para>
/// <para>
/// An entry of the slots' record with no row is an older build's, which admits its own leg while the leg is fewer from
/// the front of the line than every count up to it allows: it is counted holding exactly where that build holds it, and
/// while one waits no unit behind it is granted a slot - that build counts every unit ahead of its own, so a unit granted
/// behind it would run beside it once the units ahead of it left.
/// </para>
/// </remarks>
internal static class DispatchOrder
{
    /// <summary>The weight of every tenant of a local command, as plan 2.0 part 06 sets it ("a command outside any environment weighs 1").</summary>
    public const double LocalWeight = 1;

    /// <summary>The priority of a run whose workflow declares none, the least a run may have (part 06: "the run's priority, 1 by default").</summary>
    public const int LeastPriority = 1;

    /// <summary>
    /// What a tenant is credited each round for <paramref name="row"/> waiting at <paramref name="now"/>: its weight times its
    /// priority, doubled for each whole period of its aging bound it has waited since it asked - every period counted, so an
    /// aging bound of no minutes credits a waiting unit past any cost at once, and the units are served in the order they
    /// asked.
    /// </summary>
    /// <param name="row">The unit.</param>
    /// <param name="now">When the queue is read.</param>
    public static double ShareOf(QueuedUnit row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);

        var waited = Math.Max(0, (now - row.Holder.AskedUtc).TotalMinutes);
        var periods = row.AgeAfterMinutes > 0 ? Math.Floor(waited / row.AgeAfterMinutes) : double.PositiveInfinity;

        return row.Weight * Math.Max(LeastPriority, row.Priority) * Math.Pow(2, periods);
    }

    /// <summary>
    /// The queue as one look reads it, under the slots' record's lock: who holds and who waits, in the order served, and -
    /// where <paramref name="grant"/> - the slots left granted to the units waiting, with the record that keeps the grants
    /// and the tenants' deficits.
    /// </summary>
    /// <param name="line">The entries of the slots' record still standing, in the order they asked.</param>
    /// <param name="rows">The row of each entry this build asked; an entry with none is an older build's.</param>
    /// <param name="tenants">The waiting tenants' deficits, as the record keeps them.</param>
    /// <param name="machine">The machine's own count of heavy legs, where its settings file declares one.</param>
    /// <param name="now">When the queue is read: what the waits are counted to, and what a grant is marked with.</param>
    /// <param name="comparer">How two tenants are told apart.</param>
    /// <param name="grant">Whether the slots left are granted; a reading that only shows the queue grants none.</param>
    public static Reading Read(
        IReadOnlyList<SlotEntry> line,
        IReadOnlyDictionary<SlotEntry, QueuedUnit> rows,
        IReadOnlyList<TenantDeficit> tenants,
        int? machine,
        DateTimeOffset now,
        IEqualityComparer<string> comparer,
        bool grant)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(comparer);

        var older = OlderBuildsHolding(line, machine);
        var holders = new List<(SlotEntry Entry, int At)>();
        var candidates = new List<Waiter>();
        var behind = new List<SlotEntry>();
        var granted = new Dictionary<SlotEntry, QueuedUnit>(rows);
        var olderWaits = false;

        for (var at = 0; at < line.Count; at++)
        {
            var entry = line[at];

            if (!rows.TryGetValue(entry, out var row))
            {
                if (older.Contains(entry))
                {
                    holders.Add((entry, at));
                }
                else
                {
                    olderWaits = true;
                    behind.Add(entry);
                }

                continue;
            }

            if (row.HeldSinceUtc is not null)
            {
                holders.Add((entry, at));
            }
            else if (olderWaits)
            {
                behind.Add(entry);
            }
            else
            {
                candidates.Add(new Waiter(entry, row, at));
            }
        }

        var kept = tenants.GroupBy(tenant => tenant.Tenant, comparer).ToDictionary(group => group.Key, group => group.Last().Deficit, comparer);
        var turns = candidates
            .GroupBy(waiter => waiter.Row.Tenant, comparer)
            .Select(group => new Turn(group.Key, [.. group], kept.GetValueOrDefault(group.Key), now))
            .ToList();
        var queue = new PriorityQueue<Turn, (double Round, int At)>(Comparer<(double Round, int At)>.Create((left, right) =>
            left.Round != right.Round ? left.Round.CompareTo(right.Round) : left.At.CompareTo(right.At)));

        foreach (var turn in turns)
        {
            queue.Enqueue(turn, turn.Due);
        }

        var order = new List<SlotEntry>();
        var grantedNow = new HashSet<SlotEntry>();
        var granting = grant;
        IReadOnlyList<TenantDeficit>? stopped = grant ? null : tenants;
        var round = 0d;

        while (queue.TryPeek(out var turn, out var due))
        {
            var head = turn.Head;

            // The unit the order puts first and that does not fit holds back every unit behind it: nothing more is
            // granted at this look, and the deficits stand as they were before it.
            if (granting && !Fits(head.Entry))
            {
                granting = false;
                stopped = Deficits(round);
            }

            queue.Dequeue();
            round = due.Round;
            turn.Serve(round);
            order.Add(head.Entry);

            if (granting)
            {
                granted[head.Entry] = head.Row with { HeldSinceUtc = now };
                grantedNow.Add(head.Entry);
                holders.Add((head.Entry, head.At));
            }

            if (turn.Waiting)
            {
                queue.Enqueue(turn, turn.Due);
            }
        }

        stopped ??= Deficits(round);

        var record = new DispatchRecord(
            [.. line.Where(granted.ContainsKey).Select(entry => granted[entry])],
            stopped);

        return new Reading(
            [.. holders.OrderBy(holder => holder.At).Select(holder => holder.Entry)],
            [.. order.Where(entry => !grantedNow.Contains(entry)), .. behind],
            granted,
            record,
            comparer);

        // Whether one more unit fits beside the holders: fewer than the machine's own count where its settings file declares
        // one, else than the fewest any holder or the unit itself allows - each unit's own rule, kept whole.
        bool Fits(SlotEntry entry)
            => machine is { } count
                ? holders.Count < count
                : holders.Count < holders.Select(holder => holder.Entry.Slots).Append(entry.Slots).Min();

        // The deficit of every tenant with a unit still waiting, counted to the round the granting stopped at; a tenant with
        // none waiting is reset, and one whose units wait only behind an older build's keeps what it had.
        IReadOnlyList<TenantDeficit> Deficits(double at)
        {
            var waiting = new Dictionary<string, double>(comparer);

            foreach (var turn in turns.Where(turn => turn.Waiting))
            {
                waiting[turn.Tenant] = turn.DeficitAt(at);
            }

            foreach (var entry in behind)
            {
                if (rows.TryGetValue(entry, out var row) && !waiting.ContainsKey(row.Tenant))
                {
                    waiting[row.Tenant] = kept.GetValueOrDefault(row.Tenant);
                }
            }

            return [.. waiting.Where(tenant => tenant.Value != 0).Select(tenant => new TenantDeficit(tenant.Key, tenant.Value))];
        }
    }

    /// <summary>
    /// The entries an older build counts holding: the front of the line, each fewer legs from the front than the fewest any
    /// leg up to it allows at once - or, where the machine declares its own count, than that count - exactly as a build
    /// before the queue's own record decides it, every unit of the line counted.
    /// </summary>
    private static HashSet<SlotEntry> OlderBuildsHolding(IReadOnlyList<SlotEntry> line, int? machine)
    {
        var holding = new HashSet<SlotEntry>();
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

    /// <summary>A unit waiting to be granted a slot: its entry, its row and where it stands in the line.</summary>
    private sealed record Waiter(SlotEntry Entry, QueuedUnit Row, int At);

    /// <summary>
    /// One tenant's turn in the round-robin: its units waiting, in the order they asked, and its deficit as counted at the
    /// round it was last served at - credited lazily, its share times the rounds since, as each is asked.
    /// </summary>
    private sealed class Turn(string tenant, IReadOnlyList<Waiter> waiting, double deficit, DateTimeOffset now)
    {
        private int _next;
        private double _deficit = deficit;
        private double _round;

        public string Tenant { get; } = tenant;

        public Waiter Head => waiting[_next];

        public bool Waiting => _next < waiting.Count;

        /// <summary>The round its head is covered at, and where its head stands in the line: what the order is kept by.</summary>
        public (double Round, int At) Due => (_round + RoundsShort(_deficit, Head.Row.Cost, Share), Head.At);

        private double Share => ShareOf(Head.Row, now);

        /// <summary>Its deficit counted to <paramref name="round"/>: credited its share for each round since its last.</summary>
        public double DeficitAt(double round)
            => Waiting && round > _round ? _deficit + ((round - _round) * Share) : _deficit;

        /// <summary>Serves its head at <paramref name="round"/>: credited to it, and debited its cost.</summary>
        public void Serve(double round)
        {
            _deficit = DeficitAt(round) - Head.Row.Cost;
            _round = round;
            _next++;
        }

        // How many rounds a deficit is short of a cost by, at a share a round: none where it covers it, or the share is past
        // any cost.
        private static double RoundsShort(double deficit, double cost, double share)
            => deficit >= cost || double.IsPositiveInfinity(share) ? 0 : Math.Ceiling((cost - deficit) / share);
    }

    /// <summary>The queue as one look read it.</summary>
    /// <param name="Holders">The units holding slots, in the order they asked.</param>
    /// <param name="Waiting">
    /// The units waiting, in the order the queue serves them; the units behind an older build's waiting entry last, in
    /// the order they asked.
    /// </param>
    /// <param name="Rows">The row of each unit this build asked, its grant marked where this look granted it a slot.</param>
    /// <param name="Record">What the look keeps beside the slots' record: the rows of the units standing, and the tenants' deficits.</param>
    /// <param name="Comparer">How two tenants are told apart.</param>
    internal sealed record Reading(
        IReadOnlyList<SlotEntry> Holders,
        IReadOnlyList<SlotEntry> Waiting,
        IReadOnlyDictionary<SlotEntry, QueuedUnit> Rows,
        DispatchRecord Record,
        IEqualityComparer<string> Comparer)
    {
        /// <summary>The deficit the record keeps of <paramref name="tenant"/>; nothing where it keeps none.</summary>
        public double DeficitOf(string tenant)
            => Record.Tenants.FirstOrDefault(kept => Comparer.Equals(kept.Tenant, tenant))?.Deficit ?? 0;

        /// <summary>
        /// Where <paramref name="entry"/> stands: whether it holds, the holders, the units ahead of it in the one order - the
        /// holders, then those served before it - and how many may run at once while it waits.
        /// </summary>
        public SlotStanding StandingOf(SlotEntry entry, int? machine)
        {
            var holding = Holders.Contains(entry);
            IReadOnlyList<SlotEntry> ahead = holding
                ? [.. Holders.TakeWhile(holder => holder != entry)]
                : [.. Holders, .. Waiting.TakeWhile(waiting => waiting != entry)];

            return new SlotStanding(holding, Holders, ahead, machine ?? ahead.Select(unit => unit.Slots).Append(entry.Slots).Min());
        }
    }
}
