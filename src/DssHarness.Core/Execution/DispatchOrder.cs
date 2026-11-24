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
/// A tenant's deficit in the queue's round-robin (<see cref="DispatchOrder"/>): the credit the costs of its units are
/// charged against - below nothing by what the unit it last started cost beyond the credit its tenant had - and the share
/// that pays a debt back, each round, while the tenant has no unit waiting.
/// </summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Deficit">
/// Its credit, in minutes of cost. A debt is kept after the tenant's last unit has gone and paid back by its share each
/// round; a credit is kept only while the tenant has a unit waiting, and is spent on the next unit it starts.
/// </param>
/// <param name="Share">
/// The unaged share (<see cref="DispatchOrder.UnagedShareOf"/>) a debt is paid back at while the tenant has no unit
/// waiting: the share of the unit it last started. The share of a row a build before this one kept, where it kept none:
/// a weight and a priority of 1.
/// </param>
public sealed record TenantDeficit(string Tenant, double Deficit, double Share = DispatchOrder.LocalWeight * DispatchOrder.LeastPriority);

/// <summary>What the dispatch queue keeps beside the slots' record: a row of each unit, and each tenant's deficit.</summary>
/// <param name="Units">A row of each unit this build asked, in the order they asked.</param>
/// <param name="Tenants">Each tenant whose deficit the record keeps, waiting or not: a debt outlives its unit, a credit does not.</param>
public sealed record DispatchRecord(IReadOnlyList<QueuedUnit> Units, IReadOnlyList<TenantDeficit> Tenants)
{
    /// <summary>The record where no unit has asked.</summary>
    public static DispatchRecord Empty { get; } = new([], []);
}

/// <summary>
/// The dispatch queue's one order, and the slots it grants: deficit round-robin over the tenants of the units waiting, as
/// plan 2.0 part 06 sets it. Each round, every tenant with a unit waiting is credited its share - its unit's weight times
/// its run's priority, doubled for each whole period of the unit's aging bound it has waited - and a unit starts once its
/// tenant owes nothing, charged its estimated cost as it starts: so a tenant with a burst of short units and a tenant
/// with one long unit each get the machine in proportion, and a long unit - a WSL sweep, sent whole - leaves its tenant
/// that much behind before its next, the debt paid back by its share each round. The rounds are counted, never stepped: a
/// tenant's turn comes after as many rounds as its debt is short of nothing, over its share, and never fewer than one -
/// a share past any cost, an aging bound of no minutes, still takes the one round, so its tenant's units go one a round
/// and no tenant's setting starves another. Ties go to the unit that asked first; within a tenant, its units are served
/// in the order they asked.
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
    /// What a tenant is credited each round for <paramref name="row"/> waiting, before its waiting ages it: its weight
    /// times its run's priority - the share a debt is paid back at, and the one a credit is never carried past.
    /// </summary>
    /// <param name="row">The unit.</param>
    public static double UnagedShareOf(QueuedUnit row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.Weight * Math.Max(LeastPriority, row.Priority);
    }

    /// <summary>
    /// What a tenant is credited each round for <paramref name="row"/> waiting at <paramref name="now"/>: its unaged
    /// share (<see cref="UnagedShareOf"/>), doubled for each whole period of its aging bound it has waited since it
    /// asked - every period counted, so an aging bound of no minutes makes the share past any cost, and a debt is gone in
    /// one round.
    /// </summary>
    /// <param name="row">The unit.</param>
    /// <param name="now">When the queue is read.</param>
    public static double ShareOf(QueuedUnit row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);

        var waited = Math.Max(0, (now - row.Holder.AskedUtc).TotalMinutes);
        var periods = row.AgeAfterMinutes > 0 ? Math.Floor(waited / row.AgeAfterMinutes) : double.PositiveInfinity;

        return UnagedShareOf(row) * Math.Pow(2, periods);
    }

    /// <summary>
    /// The queue as one look reads it, under the slots' record's lock: who holds and who waits, in the order served, and -
    /// where <paramref name="grant"/> - the slots left granted to the units waiting, with the record that keeps the grants
    /// and the tenants' deficits.
    /// </summary>
    /// <param name="line">The entries of the slots' record still standing, in the order they asked.</param>
    /// <param name="rows">The row of each entry this build asked; an entry with none is an older build's.</param>
    /// <param name="tenants">The tenants' deficits, as the record keeps them.</param>
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

        var kept = tenants.GroupBy(tenant => tenant.Tenant, comparer).ToDictionary(group => group.Key, group => group.Last(), comparer);
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

        // The deficit of every tenant the order counted, to the round the granting stopped at. A tenant with a unit
        // waiting keeps its credit with its debt; one with none keeps a debt - paid back at the share the record holds -
        // and drops a credit, spent already on the unit it started; one whose units wait only behind an older build's
        // keeps what it had, its debt paid back the same way and its credit held for the units that wait.
        IReadOnlyList<TenantDeficit> Deficits(double at)
        {
            var counted = new Dictionary<string, TenantDeficit>(comparer);

            foreach (var turn in turns)
            {
                counted[turn.Tenant] = turn.Kept(at);
            }

            var waitingBehind = new HashSet<string>(behind.Where(rows.ContainsKey).Select(entry => rows[entry].Tenant), comparer);

            foreach (var standing in kept.Values)
            {
                if (counted.ContainsKey(standing.Tenant))
                {
                    continue;
                }

                var paid = standing.Deficit < 0 ? Math.Min(0, standing.Deficit + (at * standing.Share)) : standing.Deficit;

                counted[standing.Tenant] = standing with { Deficit = waitingBehind.Contains(standing.Tenant) ? paid : Math.Min(0, paid) };
            }

            return [.. counted.Where(entry => entry.Value.Deficit != 0).Select(entry => entry.Value)];
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
    /// round it was last served at - credited lazily, its share times the rounds since, as each is asked. A unit starts
    /// once its tenant owes nothing and is charged its cost as it starts, so a long unit leaves its tenant that much in
    /// debt; the debt is paid back by the share of the unit now waiting - aged, where it has waited - but no credit is
    /// ever carried past what the unaged share would give.
    /// </summary>
    private sealed class Turn(string tenant, IReadOnlyList<Waiter> waiting, TenantDeficit? kept, DateTimeOffset now)
    {
        private int _next;
        private double _deficit = kept?.Deficit ?? 0;
        private double _round;

        // The share a debt is paid back at once the tenant has nothing waiting: the share the record kept, where it kept
        // one, else the unaged share of the unit now at its head.
        private double _owedAt = kept?.Share ?? UnagedShareOf(waiting[0].Row);

        public string Tenant { get; } = tenant;

        public Waiter Head => waiting[_next];

        public bool Waiting => _next < waiting.Count;

        /// <summary>The round its head's tenant is owed nothing at, and where its head stands in the line: what the order is kept by.</summary>
        public (double Round, int At) Due => (_round + RoundsShort(_deficit, Share), Head.At);

        private double Share => ShareOf(Head.Row, now);

        private double Unaged => UnagedShareOf(Head.Row);

        /// <summary>
        /// Its deficit counted to <paramref name="round"/>: while it has a unit waiting, its share for each round since its
        /// last, never carrying a credit past what the unaged share gives; once it has none waiting, its debt paid back at
        /// the share the record keeps.
        /// </summary>
        public double DeficitAt(double round)
        {
            if (round <= _round)
            {
                return _deficit;
            }

            var rounds = round - _round;

            return Waiting
                ? Math.Min(_deficit + (rounds * Share), Math.Max(0, _deficit + (rounds * Unaged)))
                : _deficit + (rounds * _owedAt);
        }

        /// <summary>Serves its head at <paramref name="round"/>: its tenant owing nothing, the unit starts and is charged its cost.</summary>
        public void Serve(double round)
        {
            _deficit = DeficitAt(round) - Head.Row.Cost;
            _owedAt = Unaged;
            _round = round;
            _next++;
        }

        /// <summary>What the record keeps of it at <paramref name="round"/>: its deficit, and the share a debt is paid back at while it has no unit waiting.</summary>
        public TenantDeficit Kept(double round)
            => new(Tenant, Waiting ? DeficitAt(round) : Math.Min(0, DeficitAt(round)), _owedAt);

        // How many rounds a tenant is in debt for, at a share a round: none where it owes nothing, and never fewer than
        // one - a share past any cost takes the one round, so its tenant's units go one a round.
        private static double RoundsShort(double deficit, double share)
            => deficit >= 0 ? 0 : Math.Max(1, Math.Ceiling(-deficit / share));
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
