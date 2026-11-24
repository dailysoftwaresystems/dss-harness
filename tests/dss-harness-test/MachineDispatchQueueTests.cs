using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The machine's dispatch queue: a unit holds a slot once the queue grants it one, a mark written in the same change of the
/// record; the units waiting are granted the slots left by deficit round-robin over their tenants - each tenant's deficit
/// credited its weight times its priority, aged, each round, and a unit started once its tenant owes nothing, charged its
/// cost as it starts, its debt paid back by its share each round - with each unit's own room rule kept whole and the first
/// unit of the order that does not fit holding back every unit behind it. A unit's cost is the median of the newest runs
/// of its name the queue measured; an older build's entries are counted where that build holds them, and the slots' record
/// keeps the shape that build reads.
/// </summary>
public sealed class MachineDispatchQueueTests
{
    /// <summary>
    /// A tenant with a burst of short arms and a tenant with one long build each get the machine in proportion to their
    /// costs: on one slot, the build - four minutes, as its last run took - asked after the whole burst of one-minute arms
    /// starts after the burst's first arm and before its last, where the order they asked in would keep it behind the
    /// whole burst.
    /// </summary>
    [Fact]
    public void TwoTenantsOfUnequalBursts_StartInTheOrderTheirCostsShareTheMachineBy()
    {
        using var machine = new Machine(slots: 1);

        machine.RanBefore("/src/b", "build", TimeSpan.FromMinutes(4));

        var waiting = new List<SlotPlace>
        {
            machine.Ask("arm-1", "/src/a"),
            machine.Ask("arm-2", "/src/a"),
            machine.Ask("arm-3", "/src/a"),
            machine.Ask("arm-4", "/src/a"),
            machine.Ask("arm-5", "/src/a"),
            machine.Ask("build", "/src/b"),
        };

        Assert.Equal(["arm-1", "build", "arm-2", "arm-3", "arm-4", "arm-5"], machine.Starts(waiting));
    }

    /// <summary>
    /// Two tenants of units costing alike take the machine in turn, not in the order they asked: the second tenant's one
    /// unit, asked after the first's whole burst, starts second.
    /// </summary>
    [Fact]
    public void TenantsOfUnitsCostingAlike_TakeTheMachineInTurn()
    {
        using var machine = new Machine(slots: 1);

        var waiting = new List<SlotPlace>
        {
            machine.Ask("a-1", "/src/a"),
            machine.Ask("a-2", "/src/a"),
            machine.Ask("a-3", "/src/a"),
            machine.Ask("b-1", "/src/b"),
        };

        Assert.Equal(["a-1", "b-1", "a-2", "a-3"], machine.Starts(waiting));
    }

    /// <summary>
    /// A heavier tenant is credited its weight each round, so it is served in proportion: two of its units run between
    /// the lighter tenant's second and its last, where equal weights take turns.
    /// </summary>
    [Fact]
    public void AHeavierTenant_IsServedInProportionToItsWeight()
    {
        using var machine = new Machine(slots: 1);

        var waiting = new List<SlotPlace>
        {
            machine.Ask("light-1", "/src/light"),
            machine.Ask("light-2", "/src/light"),
            machine.Ask("light-3", "/src/light"),
            machine.Ask("heavy-1", "/src/heavy", weight: 2),
            machine.Ask("heavy-2", "/src/heavy", weight: 2),
            machine.Ask("heavy-3", "/src/heavy", weight: 2),
        };

        Assert.Equal(["light-1", "heavy-1", "light-2", "heavy-2", "heavy-3", "light-3"], machine.Starts(waiting));
    }

    /// <summary>A run its workflow marks with a higher priority is credited it each round, as a weight is.</summary>
    [Fact]
    public void AHigherPriorityRun_IsServedInProportionToItsPriority()
    {
        using var machine = new Machine(slots: 1);

        var waiting = new List<SlotPlace>
        {
            machine.Ask("later-1", "/src/later"),
            machine.Ask("later-2", "/src/later"),
            machine.Ask("later-3", "/src/later"),
            machine.Ask("urgent-1", "/src/urgent", priority: 2),
            machine.Ask("urgent-2", "/src/urgent", priority: 2),
            machine.Ask("urgent-3", "/src/urgent", priority: 2),
        };

        Assert.Equal(["later-1", "urgent-1", "later-2", "urgent-2", "urgent-3", "later-3"], machine.Starts(waiting));
    }

    /// <summary>
    /// A unit waiting past its aging bound has its tenant's share doubled for each whole period, so its debt is paid back
    /// the sooner the longer it has waited: an eight-minute build, granted once and asked again while 25 minutes past a
    /// bound of 10 - two periods, four times the share - is served after two short arms of the tenant asking after it,
    /// where a bound no wait doubles under leaves it behind all three. Aging never carries a credit past the debt: the
    /// arms' tenant, owing nothing of its own, is never pushed behind by the build's aging.
    /// </summary>
    [Theory]
    [InlineData(10, new[] { "arm-1", "arm-2", "build-2", "arm-3" })]
    [InlineData(100_000, new[] { "arm-1", "arm-2", "arm-3", "build-2" })]
    public void AUnitWaitingPastItsAgingBound_HasItsTenantsShareDoubledForEachPeriod(double ageAfterMinutes, string[] starts)
    {
        using var machine = new Machine(slots: 1);
        var ageAfter = TimeSpan.FromMinutes(ageAfterMinutes);

        machine.RanBefore("/src/old", "build-1", TimeSpan.FromMinutes(8));

        var build1 = machine.Ask("build-1", "/src/old", ageAfter: ageAfter);

        Assert.True(machine.Queue.Look(build1).Holding);

        var build2 = machine.Ask("build-2", "/src/old", ageAfter: ageAfter);

        machine.Clock.Advance(TimeSpan.FromMinutes(25));

        List<SlotPlace> waiting =
        [
            build2,
            machine.Ask("arm-1", "/src/new", ageAfter: ageAfter),
            machine.Ask("arm-2", "/src/new", ageAfter: ageAfter),
            machine.Ask("arm-3", "/src/new", ageAfter: ageAfter),
        ];

        build1.Dispose();

        Assert.Equal(starts, machine.Starts(waiting));
    }

    /// <summary>
    /// An aging bound of no minutes makes the share past any cost, and a share past any cost still takes a round: a
    /// tenant's debt is gone in one round, though no round credits it past its weight times its priority, and its units go
    /// in the order they asked, the tenants in turn. At priority 1, units of a quantum go one a round - the second tenant's
    /// served between the first's, where a share due in no rounds would serve the first's whole burst before them; at
    /// priority 2, the round that clears the first tenant's debt leaves it a quantum's credit, and two of its units start
    /// between the second tenant's two.
    /// </summary>
    [Theory]
    [InlineData(1, new[] { "a-1", "b-1", "a-2", "b-2", "a-3", "a-4" })]
    [InlineData(2, new[] { "a-1", "b-1", "a-2", "a-3", "b-2", "a-4" })]
    public void AnAgingBoundOfNoMinutes_ClearsADebtInOneRound_CreditingNoMoreThanTheUnagedShare(int priority, string[] starts)
    {
        using var machine = new Machine(slots: 1);

        var waiting = new List<SlotPlace>
        {
            machine.Ask("a-1", "/src/a", priority: priority, ageAfter: TimeSpan.Zero),
            machine.Ask("a-2", "/src/a", priority: priority, ageAfter: TimeSpan.Zero),
            machine.Ask("a-3", "/src/a", priority: priority, ageAfter: TimeSpan.Zero),
            machine.Ask("a-4", "/src/a", priority: priority, ageAfter: TimeSpan.Zero),
            machine.Ask("b-1", "/src/b", ageAfter: TimeSpan.Zero),
            machine.Ask("b-2", "/src/b", ageAfter: TimeSpan.Zero),
        };

        Assert.Equal(starts, machine.Starts(waiting));
    }

    /// <summary>
    /// No tenant's setting starves another: a tenant whose aging bound is no minutes - its share past any cost - asks a
    /// stream of units against one waiting at the default bound with a three-minute build behind it, and still gets one
    /// round in two once the other's turn comes, its stream of quanta held to a unit a round - where a share due in no
    /// rounds would serve the whole stream before the other tenant's first build.
    /// </summary>
    [Fact]
    public void AnUnboundedTenantsStream_DoesNotStarveATenantOfTheDefaultBound()
    {
        using var machine = new Machine(slots: 1);

        machine.RanBefore("/src/b", "b-1", TimeSpan.FromMinutes(3));
        machine.RanBefore("/src/b", "b-2", TimeSpan.FromMinutes(3));

        var waiting = new List<SlotPlace>
        {
            machine.Ask("a-1", "/src/a", ageAfter: TimeSpan.Zero),
            machine.Ask("a-2", "/src/a", ageAfter: TimeSpan.Zero),
            machine.Ask("a-3", "/src/a", ageAfter: TimeSpan.Zero),
            machine.Ask("a-4", "/src/a", ageAfter: TimeSpan.Zero),
            machine.Ask("a-5", "/src/a", ageAfter: TimeSpan.Zero),
            machine.Ask("a-6", "/src/a", ageAfter: TimeSpan.Zero),
            machine.Ask("b-1", "/src/b"),
            machine.Ask("b-2", "/src/b"),
        };

        Assert.Equal(["a-1", "b-1", "a-2", "a-3", "a-4", "b-2", "a-5", "a-6"], machine.Starts(waiting));
    }

    /// <summary>
    /// A blocked head is not passed for ever by an unbounded tenant's arrivals: two units whose own rule allows three
    /// hold, a third allowing one asks behind them, and a unit of the unbounded tenant asking after it - which a share
    /// due in no rounds would have passed it - is not granted while the head waits, for its share too takes a round.
    /// </summary>
    [Fact]
    public void ABlockedHead_IsNotPassedByAnUnboundedTenantsArrivals()
    {
        using var machine = new Machine(slots: null);
        var first = machine.Ask("a-1", "/src/a", slots: 3, ageAfter: TimeSpan.Zero);
        var second = machine.Ask("a-2", "/src/a", slots: 3, ageAfter: TimeSpan.Zero);

        Assert.True(machine.Queue.Look(first).Holding);
        Assert.True(machine.Queue.Look(second).Holding);

        var blocked = machine.Ask("b-1", "/src/b", slots: 1);
        var arrival = machine.Ask("a-3", "/src/a", slots: 3, ageAfter: TimeSpan.Zero);

        Assert.False(machine.Queue.Look(blocked).Holding);
        Assert.False(machine.Queue.Look(arrival).Holding);

        first.Dispose();
        second.Dispose();

        Assert.True(machine.Queue.Look(blocked).Holding);
        Assert.False(machine.Queue.Look(arrival).Holding);
    }

    /// <summary>
    /// A WSL sweep, sent whole as one unit, is charged its whole cost as it starts, and its tenant's charge outlives it:
    /// a four-minute sweep granted on an idle machine leaves its tenant four minutes in debt, so - the clock held still,
    /// no wait aging its next unit - the four one-minute builds the other tenant asked while it held start before its next
    /// unit, which asks before two more of that tenant's and behind none of the four it owes; where a charge dropped with
    /// its unit would leave the sweep's tenant owing nothing, its next unit starting at once against the other tenant's
    /// turn.
    /// </summary>
    [Fact]
    public void AWslSweep_IsChargedWhole_ItsTenantsNextUnitWaitingForTheOtherTenantsUnitsOfThatCost()
    {
        using var machine = new Machine(slots: 1);

        machine.Measure("/src/a", "sweep", TimeSpan.FromMinutes(4));
        machine.Measure("/src/b", "build", TimeSpan.FromMinutes(1));

        var sweep = machine.Ask("sweep", "/src/a");

        Assert.True(machine.Queue.Look(sweep).Holding);

        List<SlotPlace> waiting =
        [
            machine.Ask("b-1", "/src/b"),
            machine.Ask("b-2", "/src/b"),
            machine.Ask("b-3", "/src/b"),
            machine.Ask("b-4", "/src/b"),
        ];

        foreach (var build in waiting)
        {
            Assert.False(machine.Queue.Look(build).Holding);
        }

        sweep.Dispose();

        waiting.AddRange(
        [
            machine.Ask("a-next", "/src/a"),
            machine.Ask("b-5", "/src/b"),
            machine.Ask("b-6", "/src/b"),
        ]);

        Assert.Equal(["b-1", "b-2", "b-3", "b-4", "a-next", "b-5", "b-6"], machine.Starts(waiting));
    }

    /// <summary>
    /// A debt its tenant carries is repaid at the aged share once its next unit has waited past the bound, as the clock
    /// moves: an eight-minute WSL sweep granted on an idle machine leaves its tenant eight minutes in debt, and its next
    /// unit, asked as the sweep ends behind eight one-minute builds of another tenant that each hold the one slot five
    /// minutes, starts after five of them at a bound of ten - its share doubled at ten minutes of waiting and again at
    /// twenty - where a bound no wait reaches repays the debt at the unaged share, a quantum a round, and leaves it behind
    /// all eight. Aging is what keeps a unit waiting behind its tenant's long debt from starving.
    /// </summary>
    [Theory]
    [InlineData(10, new[] { "b-1", "b-2", "b-3", "b-4", "b-5", "a-next", "b-6", "b-7", "b-8" })]
    [InlineData(100_000, new[] { "b-1", "b-2", "b-3", "b-4", "b-5", "b-6", "b-7", "b-8", "a-next" })]
    public void ACarriedDebt_IsRepaidAtTheAgedShare_OnceItsTenantsNextUnitHasWaitedPastTheBound(double ageAfterMinutes, string[] starts)
    {
        using var machine = new Machine(slots: 1);

        machine.RanBefore("/src/a", "sweep", TimeSpan.FromMinutes(8));

        var sweep = machine.Ask("sweep", "/src/a");

        Assert.True(machine.Queue.Look(sweep).Holding);

        List<SlotPlace> waiting = [.. Enumerable.Range(1, 8).Select(build => machine.Ask($"b-{build}", "/src/b"))];

        machine.Clock.Advance(TimeSpan.FromMinutes(8));
        sweep.Dispose();
        waiting.Add(machine.Ask("a-next", "/src/a", ageAfter: TimeSpan.FromMinutes(ageAfterMinutes)));

        Assert.Equal(starts, machine.Starts(waiting, held: TimeSpan.FromMinutes(5)));
    }

    /// <summary>
    /// A tenant is a local command's repository spelt one way on every machine - its separators forward and none at its
    /// ends, under <c>local</c>, a Windows checkout's drive and a share's server kept - and <c>local</c> alone where no
    /// repository was said.
    /// </summary>
    [Theory]
    [InlineData(@"C:\src\mine", "local/C:/src/mine")]
    [InlineData("/src/mine/", "local/src/mine")]
    [InlineData(@"\\server\share\repo", "local/server/share/repo")]
    [InlineData(null, "local")]
    [InlineData(" ", "local")]
    public void ATenant_IsTheRepositorySpeltOneWay(string? repository, string tenant)
        => Assert.Equal(tenant, MachineDispatchQueue.TenantOf(repository));

    /// <summary>
    /// Where a unit waits names who stands ahead of it, as a wait on the queue is told: each unit ahead's tenant, once
    /// each, in the order they stand - an entry an older build asked named by the local tenant, whose repository was not
    /// said - and nothing where the unit holds, for nothing waits on its place.
    /// </summary>
    [Fact]
    public void WhereAUnitWaits_NamesTheTenantsAheadOfIt_AsAWaitOnTheQueueIsTold()
    {
        using var machine = new Machine(slots: 2);

        AdmissionKit.Write(machine.Record, AdmissionKit.Holder(machine.Harness, "older"));
        var holding = machine.Ask("a-1", "/src/a");

        Assert.True(machine.Queue.Look(holding).Holding);

        var sameTenant = machine.Ask("a-2", "/src/a");
        var other = machine.Ask("b-1", "/src/b");
        var standing = machine.Queue.Look(other);

        Assert.False(standing.Holding);
        Assert.Equal(new QueueAhead(DispatchClassName.Heavy, ["local", "local/src/a"]), standing.TenantsAhead);
        Assert.Equal(new QueueAhead(DispatchClassName.Heavy, ["local", "local/src/a", "local/src/b"]), machine.Queue.Look(sameTenant).TenantsAhead);
        Assert.Null(machine.Queue.Look(holding).TenantsAhead);
    }

    /// <summary>
    /// A share past any number - an aging bound of no minutes, or more doublings than a number holds - is said as past
    /// any cost, and its tenant's debt is gone in one round: the queue says it as past any cost, and its document holds
    /// no number for it, which no JSON could.
    /// </summary>
    [Fact]
    public void AShareNoNumberHolds_IsSaidAsPastAnyCost()
    {
        using var machine = new Machine(slots: 1);
        var holding = machine.Ask("a-1", "/src/a");

        Assert.True(machine.Queue.Look(holding).Holding);

        var unbounded = machine.Ask("b-1", "/src/b", ageAfter: TimeSpan.Zero);
        var doubled = machine.Ask("c-1", "/src/c", ageAfter: TimeSpan.FromMinutes(1));
        var waited = TimeSpan.FromMinutes(1024);

        machine.Clock.Advance(waited);

        var classes = machine.Queue.Queue(AdmissionKit.Rule());
        var document = JsonNode.Parse(Assert.Single(QueueReports.Render(classes, json: true).Data))!;

        Assert.Equal([double.PositiveInfinity, double.PositiveInfinity], classes[0].Waiting.Select(unit => unit.Share ?? 0));
        Assert.All(document["queue"]![0]!["waiting"]!.AsArray(), unit => Assert.Null(unit!["share"]));
        Assert.Equal(
            [
                $"  waiting 1 (tenant 'local/src/b', cost 1, share past any cost, deficit 0, weight 1, priority 1, waited {LedgerReport.SaidDuration(waited)}): {unbounded.Entry.Describe()}",
                $"  waiting 2 (tenant 'local/src/c', cost 1, share past any cost, deficit 0, weight 1, priority 1, waited {LedgerReport.SaidDuration(waited)}): {doubled.Entry.Describe()}",
            ],
            QueueReports.Render(classes, json: false).Data.Skip(2).Take(2));
    }

    /// <summary>
    /// A unit granted a slot holds it until it gives it back, whatever arrives after it: a tenant of higher priority asking
    /// for the one slot finds it held, and waits behind it - nothing is pre-empted.
    /// </summary>
    [Fact]
    public void AGrantedUnit_HoldsItsSlot_WhateverArrivesAfterIt()
    {
        using var machine = new Machine(slots: 1);

        machine.Measure("/src/long", "build", TimeSpan.FromMinutes(40));

        var holding = machine.Ask("build", "/src/long");

        Assert.True(machine.Queue.Look(holding).Holding);

        var urgent = machine.Ask("urgent", "/src/urgent", priority: 32);
        var waits = machine.Queue.Look(urgent);
        var still = machine.Queue.Look(holding);

        Assert.Equal((false, 1, 1), (waits.Holding, waits.Ahead, waits.Slots));
        Assert.Equal([holding.Entry], waits.Holders);
        Assert.True(still.Holding);
    }

    /// <summary>
    /// Each unit's own room rule is kept whole, and the first unit of the order that does not fit holds back every unit
    /// behind it: a unit whose repository allows one heavy leg at once waits for the two holding to end, and the unit of
    /// three asking after it - which would fit - is not granted meanwhile; once they end, the one runs alone.
    /// </summary>
    [Fact]
    public void TheFirstUnitThatDoesNotFit_HoldsBackEveryUnitBehindIt_UntilItFits()
    {
        using var machine = new Machine(slots: null);
        var first = machine.Ask("a-1", "/src/a", slots: 3);
        var second = machine.Ask("a-2", "/src/a", slots: 3);

        Assert.True(machine.Queue.Look(first).Holding);
        Assert.True(machine.Queue.Look(second).Holding);

        var alone = machine.Ask("b-1", "/src/b", slots: 1);
        var third = machine.Ask("a-3", "/src/a", slots: 3);
        var behind = machine.Queue.Look(third);

        Assert.Equal((false, 3, 1), (behind.Holding, behind.Ahead, behind.Slots));
        Assert.Equal([first.Entry, second.Entry, alone.Entry], behind.InLineAhead);
        Assert.False(machine.Queue.Look(alone).Holding);

        first.Dispose();
        Assert.False(machine.Queue.Look(alone).Holding);
        second.Dispose();

        Assert.True(machine.Queue.Look(alone).Holding);
        Assert.Equal((false, 1), (machine.Queue.Look(third).Holding, machine.Queue.Look(third).Slots));
    }

    /// <summary>
    /// The heavy legs admitted through the admission's own wait are those the queue grants: on one slot, a leg of a second
    /// tenant asked after two units of the first is the first of the order - the first's earlier unit has left its tenant
    /// in debt, and this one's owes nothing - so it waits on the holder alone, one unit ahead of it, and is admitted once
    /// that holder ends, not after the first tenant's whole burst.
    /// </summary>
    [Fact]
    public async Task ALegOfASecondTenant_IsAdmittedInItsTurn_BeforeTheFirstTenantsLaterUnit()
    {
        using var machine = new Machine(slots: 1);
        var holding = machine.Ask("a-1", "/src/a");

        Assert.True(machine.Queue.Look(holding).Holding);

        var next = machine.Ask("a-2", "/src/a");
        var later = machine.Ask("a-3", "/src/a");
        var said = new List<string>();
        var waits = 0;

        // The holder ends at the first wait, and the leg is granted at the look after it.
        var admission = AdmissionKit.Admission(machine.Queue, new ScriptedGauge(10), machine.Clock, settle: TimeSpan.Zero, onWait: () =>
        {
            if (++waits == 1)
            {
                holding.Dispose();
            }
        });

        using var admitted = await admission.AdmitAsync(
            AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 1, settleLeast: 0, settleMost: 0), said, leg: "b-1", repository: "/src/b"),
            TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(AdmissionSettings.DefaultPollSeconds, admitted.Fact.WaitedSeconds);
        Assert.StartsWith(
            $"waits for one of this machine's 1 heavy-leg slot(s) as tenant 'local/src/b', 1 leg(s) ahead; held by {holding.Entry.Describe()}",
            said[0],
            StringComparison.Ordinal);
        Assert.False(machine.Queue.Look(later).Holding);
        Assert.False(machine.Queue.Look(next).Holding);
    }

    /// <summary>
    /// A leg's unit is weighed by what its rule and its run declare: the aging bound its admission section says, its run's
    /// priority and its repository's tenant, each kept in the queue's row of it as the leg is admitted.
    /// </summary>
    [Fact]
    public async Task ALegsUnit_IsWeighedByItsRulesAgingBound_ItsRunsPriority_AndItsRepository()
    {
        using var machine = new Machine(slots: 2);
        var admission = AdmissionKit.Admission(machine.Queue, new ScriptedGauge(10), machine.Clock, settle: TimeSpan.Zero);
        var rule = AdmissionKit.Rule(settleLeast: 0, settleMost: 0) with { AgeAfter = TimeSpan.FromMinutes(5) };

        using var admitted = await admission.AdmitAsync(
            AdmissionKit.Request(rule, [], leg: "b-1", repository: "/src/b", priority: 4),
            TestContext.Current.CancellationToken);

        var row = Assert.Single(JsonSerializer.Deserialize<DispatchRecord>(File.ReadAllText(MachineDispatchQueue.QueuePathFor(machine.Record)), JsonStateFile.Options)!.Units);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(("local/src/b", 4, 5d, (DateTimeOffset?)machine.Clock.GetUtcNow()), (row.Tenant, row.Priority, row.AgeAfterMinutes, row.HeldSinceUtc));
    }

    /// <summary>
    /// A unit's cost is the median of the newest ten runs of its name the queue measured - from when it was let start to
    /// when it gave its slot back - by when each ended, never the longest ten, and one quantum where none is kept: kept for
    /// its repository, its command and its name, so a test of the leg, another repository's build of it and an arm of a
    /// sweep of it are each a history of their own.
    /// </summary>
    [Fact]
    public void AUnitsCost_IsTheMedianOfTheNewestTenRunsOfItsName()
    {
        using var machine = new Machine(slots: 4);

        // Twelve builds of the leg, the two oldest the longest (30 and 20 minutes), the rest 3 to 12: the newest ten are 3
        // to 12, whose median is 7.5, where the ten longest would make it 9.5 and every run counted 9.5.
        foreach (var minutes in new[] { 30, 20, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 })
        {
            machine.Measure("/src/a", "linux", TimeSpan.FromMinutes(minutes));
        }

        machine.Measure("/src/a", "linux", TimeSpan.FromMinutes(99), command: "test");
        machine.Measure("/src/b", "linux", TimeSpan.FromMinutes(42));
        machine.Measure("/src/a", "linux/arm-7", TimeSpan.FromSeconds(30), command: "check-mutations");

        Assert.Equal(
            [7.5, 99, 42, 0.5, UnitDurations.Quantum, UnitDurations.Quantum],
            [
                machine.Ask("linux", "/src/a").Unit.Cost,
                machine.Ask("linux", "/src/a", command: "test").Unit.Cost,
                machine.Ask("linux", "/src/b").Unit.Cost,
                machine.Ask("linux/arm-7", "/src/a", command: "check-mutations").Unit.Cost,
                machine.Ask("linux/arm-8", "/src/a", command: "check-mutations").Unit.Cost,
                machine.Ask("windows", "/src/a").Unit.Cost,
            ]);
    }

    /// <summary>
    /// A record of a unit's runs that cannot be read is said, naming it, and the unit is charged one quantum and admitted:
    /// the estimate orders the queue, and never refuses a leg.
    /// </summary>
    [Fact]
    public void AHistoryThatCannotBeRead_IsSaid_AndChargesOneQuantum()
    {
        using var machine = new Machine(slots: 1);

        machine.Measure("/src/a", "linux", TimeSpan.FromMinutes(5));

        var history = Assert.Single(Directory.GetFiles(MachineDispatchQueue.DurationsPathFor(machine.Record)));

        File.WriteAllText(history, "not a record");

        var place = machine.Ask("linux", "/src/a");

        Assert.Equal(UnitDurations.Quantum, place.Unit.Cost);
        Assert.True(machine.Queue.Look(place).Holding);

        // The reader's own words between them are the runtime's.
        var said = Assert.Single(machine.Harness.StandardError.ToString().Split(Environment.NewLine), line => line.Contains("WARN", StringComparison.Ordinal));

        Assert.StartsWith($"admission: WARN - The record of how long 'linux' held its slot, '{history}', could not be read: ", said, StringComparison.Ordinal);
        Assert.EndsWith(". It is charged one quantum.", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The slots' record this build writes holds only the members a build before the queue's own record reads - its reader
    /// refuses a member it does not know - with a unit asked, granted and waiting; what the queue keeps of each unit is in
    /// the record beside it.
    /// </summary>
    [Fact]
    public void TheSlotsRecordThisBuildWrites_ReadsUnderTheMembersAnOlderBuildKnows()
    {
        using var machine = new Machine(slots: 1);
        var holding = machine.Ask("a-1", "/src/a", priority: 3);
        var waiting = machine.Ask("b-1", "/src/b");

        Assert.True(machine.Queue.Look(holding).Holding);
        Assert.False(machine.Queue.Look(waiting).Holding);

        var older = JsonSerializer.Deserialize<List<OlderSlotEntry>>(File.ReadAllText(machine.Record), JsonStateFile.Options)!;
        var kept = JsonSerializer.Deserialize<DispatchRecord>(File.ReadAllText(MachineDispatchQueue.QueuePathFor(machine.Record)), JsonStateFile.Options)!;

        Assert.Equal([holding.Entry.Leg, waiting.Entry.Leg], older.Select(entry => entry.Leg));
        Assert.Equal(
            [holding.Unit with { HeldSinceUtc = machine.Clock.GetUtcNow() }, waiting.Unit],
            kept.Units);
        Assert.Equal(("local/src/a", 3), (kept.Units[0].Tenant, kept.Units[0].Priority));
    }

    /// <summary>
    /// An entry an older build wrote - no row beside it - holds exactly where that build holds it, the front of the line,
    /// and while one waits no unit behind it is granted a slot, though the fair order would pass it: that build counts every
    /// unit ahead of its own, so a unit granted behind it would run beside it once the units ahead left.
    /// </summary>
    [Fact]
    public void AnOlderBuildsEntries_HoldWhereThatBuildHoldsThem_AndNoUnitPassesOneWaiting()
    {
        using var machine = new Machine(slots: 1);

        AdmissionKit.Write(machine.Record, AdmissionKit.Holder(machine.Harness, "older-1"), AdmissionKit.Holder(machine.Harness, "older-2"));

        var mine = machine.Ask("mine", "/src/mine");
        var behind = machine.Queue.Look(mine);

        Assert.Equal((false, 2), (behind.Holding, behind.Ahead));
        Assert.Equal(["older-1"], behind.Holders.Select(holder => holder.Leg));
        Assert.Equal([("older-1", true), ("older-2", false), ("mine", false)], machine.Queue.Line().Select(unit => (unit.Entry.Leg, unit.HoldsSlot)));

        AdmissionKit.Write(machine.Record, [.. AdmissionKit.Read(machine.Record).Where(entry => entry.Leg != "older-1")]);

        Assert.Equal(["older-2"], machine.Queue.Look(mine).Holders.Select(holder => holder.Leg));

        AdmissionKit.Write(machine.Record, [.. AdmissionKit.Read(machine.Record).Where(entry => entry.Leg != "older-2")]);

        Assert.True(machine.Queue.Look(mine).Holding);
    }

    /// <summary>
    /// Two processes asking one machine's record each see the other's holders: a unit another process was granted holds
    /// the one slot against this process's unit, and once that process has ended its slot is reclaimed by the next look,
    /// its grant with it, and the unit waiting is granted it.
    /// </summary>
    [Fact]
    public void AHolderWhoseProcessEnded_IsReclaimed_AndItsSlotGrantedToTheNextUnit()
    {
        using var machine = new Machine(slots: 1);
        var processes = new Processes(machine.Harness.Identity);
        var other = new MachineDispatchQueue(machine.Harness.FileSystem, machine.Harness.Output, processes.As(4242), machine.Record, () => 1, clock: machine.Clock);
        var here = new MachineDispatchQueue(machine.Harness.FileSystem, machine.Harness.Output, processes.As(machine.Harness.Identity.CurrentId), machine.Record, () => 1, clock: machine.Clock);
        var theirs = other.Ask("run-theirs", "build", "theirs", "local", "/src/theirs", null, 2, repository: "/src/theirs");

        Assert.True(other.Look(theirs).Holding);

        var mine = here.Ask("run-mine", "build", "mine", "local", "/src/mine", null, 2, repository: "/src/mine");

        var waits = here.Look(mine);

        Assert.False(waits.Holding);
        Assert.Equal([theirs.Entry], waits.Holders);

        processes.End(4242);

        Assert.True(here.Look(mine).Holding);
        Assert.Equal(["mine"], AdmissionKit.Read(machine.Record).Select(entry => entry.Leg));
    }

    /// <summary>
    /// The queue as the command reads it: each class with its capacity - the heavy class's the machine's own count - its
    /// holders in the order they asked and its waiting units in the order served, each with its tenant, cost, share and its
    /// tenant's deficit, an older build's entry said as that build's; the process and agent classes the rule's, asked by no
    /// unit yet.
    /// </summary>
    [Fact]
    public void TheQueue_AnswersEachClass_WithWhatItsOrderRestsOn()
    {
        using var machine = new Machine(slots: 2);

        AdmissionKit.Write(machine.Record, AdmissionKit.Holder(machine.Harness, "older"));

        var holding = machine.Ask("a-1", "/src/a");

        Assert.True(machine.Queue.Look(holding).Holding);

        machine.Ask("a-2", "/src/a");
        machine.Ask("b-1", "/src/b", priority: 2);
        machine.Clock.Advance(TimeSpan.FromMinutes(3));

        var classes = machine.Queue.Queue(AdmissionKit.Rule() with { Processes = 12, Agents = 3 });
        var heavy = classes[0];

        Assert.Equal(
            [(DispatchClassName.Heavy, 2, true), (DispatchClassName.Process, 12, false), (DispatchClassName.Agent, 3, false)],
            classes.Select(@class => (@class.Class, @class.Capacity ?? -1, @class.Asked)));
        Assert.Equal(
            [("older", (string?)null), ("a-1", "local/src/a")],
            heavy.Holders.Select(unit => (unit.Entry.Leg, unit.Queued?.Tenant)));
        // The waiting order serves b-1 first: it asked after a-1, but a-1's cost has left its tenant a quantum in debt
        // and b-1's owes nothing; the priority shows in the share, which tells how fast that deficit grows.
        (string Leg, string Tenant, double Cost, double? Share, double? Deficit, TimeSpan? Waited)[] waiting =
        [
            ("b-1", "local/src/b", 1, 2, 0, TimeSpan.FromMinutes(3)),
            ("a-2", "local/src/a", 1, 1, -1, TimeSpan.FromMinutes(3)),
        ];

        Assert.Equal(waiting, heavy.Waiting.Select(unit => (unit.Entry.Leg, unit.Queued!.Tenant, unit.Queued.Cost, unit.Share, unit.Deficit, unit.Waited)));
        Assert.Empty(classes[1].Holders.Concat(classes[1].Waiting).Concat(classes[2].Holders).Concat(classes[2].Waiting));
    }

    /// <summary>
    /// The heavy class's capacity where the machine declares no count of its own is the slots' own rule - the fewest any
    /// holder or the next unit served allows - and, with no unit in line, the count of the rule the command reads; where no
    /// section declares admission, none: every unit at once.
    /// </summary>
    [Fact]
    public void TheHeavyCapacity_IsTheFewestAHolderOrTheNextUnitAllows_ElseTheRulesCount()
    {
        using var machine = new Machine(slots: null);

        Assert.Equal(new int?[] { 3, null }, new[] { AdmissionKit.Rule(heavyLegs: 3), null }.Select(rule => machine.Queue.Queue(rule)[0].Capacity));

        var wide = machine.Ask("wide", "/src/a", slots: 4);

        Assert.True(machine.Queue.Look(wide).Holding);
        machine.Ask("narrow", "/src/b", slots: 1);

        Assert.Equal(1, machine.Queue.Queue(AdmissionKit.Rule(heavyLegs: 3))[0].Capacity);
    }

    /// <summary>
    /// The queue command's answer: asked for <c>--json</c>, its one document whole - every class with its capacity, whether
    /// any unit asks it, its holders and its waiting units, each unit's tenant, weight, priority, cost, share, deficit, grant,
    /// wait in seconds and entry - after the code and the line it ends on; as text, a line a unit under its class, the
    /// waiting ones numbered in the order served. The stopped document is the same shape, holding no classes.
    /// </summary>
    [Fact]
    public void TheQueueCommandsAnswer_IsItsDocumentWhole_WhereAsked_ElseItsListing()
    {
        var asked = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var holder = new SlotEntry("m", 7, "r1", asked, "build", "linux", "local", "/src/a", 2);
        var waiter = new SlotEntry("m", 8, "r2", asked, "test", "linux", "local", "/src/b", 2, Variant: "release");
        var older = new SlotEntry("m", 9, "r3", asked, "test", "mac", "local", "/src/c", 2);
        IReadOnlyList<DispatchClass> classes =
        [
            new(
                DispatchClassName.Heavy,
                2,
                [new DispatchUnit(holder, new QueuedUnit(holder, "local/src/a", 1, 4, 10) { HeldSinceUtc = asked }, null, null, null)],
                [
                    new DispatchUnit(waiter, new QueuedUnit(waiter, "local/src/b", 3, 1.5, 10) { Weight = 2 }, TimeSpan.FromSeconds(185), 6, 0.25),
                    new DispatchUnit(older, null, TimeSpan.FromSeconds(60), null, null),
                ],
                Asked: true),
            new(DispatchClassName.Agent, null, [], [], Asked: false),
        ];
        const string Summary = "1 unit(s) holding and 2 unit(s) waiting, in 2 class(es)";

        var json = QueueReports.Render(classes, json: true);
        var text = QueueReports.Render(classes, json: false);

        Assert.Equal((HarnessExit.Success, Summary, true), (json.ExitCode, json.Message, json.Quiet));
        Assert.Equal(
            """{"exitCode":0,"summary":"1 unit(s) holding and 2 unit(s) waiting, in 2 class(es)","queue":[{"class":"heavy","capacity":2,"asked":true,"holding":[{"tenant":"local/src/a","olderBuild":false,"weight":1,"priority":1,"cost":4,"share":null,"deficit":null,"heldSinceUtc":"2026-11-23T12:00:00+00:00","waitedSeconds":null,"askedUtc":"2026-11-23T12:00:00+00:00","command":"build","leg":"linux","host":"local","tree":"/src/a","variant":null,"processId":7,"runId":"r1","unit":"'/src/a' on local (leg 'linux', build, pid 7, run r1, since 2026-11-23 12:00:00Z)"}],"waiting":[{"tenant":"local/src/b","olderBuild":false,"weight":2,"priority":3,"cost":1.5,"share":6,"deficit":0.25,"heldSinceUtc":null,"waitedSeconds":185,"askedUtc":"2026-11-23T12:00:00+00:00","command":"test","leg":"linux","host":"local","tree":"/src/b","variant":"release","processId":8,"runId":"r2","unit":"'/src/b' variant 'release' on local (leg 'linux', test, pid 8, run r2, since 2026-11-23 12:00:00Z)"},{"tenant":null,"olderBuild":true,"weight":null,"priority":null,"cost":null,"share":null,"deficit":null,"heldSinceUtc":null,"waitedSeconds":60,"askedUtc":"2026-11-23T12:00:00+00:00","command":"test","leg":"mac","host":"local","tree":"/src/c","variant":null,"processId":9,"runId":"r3","unit":"'/src/c' on local (leg 'mac', test, pid 9, run r3, since 2026-11-23 12:00:00Z)"}]},{"class":"agent","capacity":null,"asked":false,"holding":[],"waiting":[]}]}""",
            Compact(Assert.Single(json.Data)));
        Assert.Equal((HarnessExit.Success, Summary, false), (text.ExitCode, text.Message, text.Quiet));
        Assert.Equal(
            [
                "heavy: 2 unit(s) at once",
                "  holding (tenant 'local/src/a', cost 4, weight 1, priority 1): '/src/a' on local (leg 'linux', build, pid 7, run r1, since 2026-11-23 12:00:00Z)",
                "  waiting 1 (tenant 'local/src/b', cost 1.5, share 6, deficit 0.25, weight 2, priority 3, waited 3m05s): '/src/b' variant 'release' on local (leg 'linux', test, pid 8, run r2, since 2026-11-23 12:00:00Z)",
                "  waiting 2 (asked by an older build, which admits it in the order the line was asked in, waited 1m00s): '/src/c' on local (leg 'mac', test, pid 9, run r3, since 2026-11-23 12:00:00Z)",
                "agent: no section declares admission, so every unit at once, asked by no unit yet",
            ],
            text.Data);
        Assert.Equal(
            """{"exitCode":13,"summary":"stopped","queue":null}""",
            Compact(QueueReports.Stopped(HarnessExit.Refused, "stopped")));
    }

    /// <summary>A JSON document on one line, its text as written: what an exact comparison of a document holds it to.</summary>
    private static string Compact(string document)
        => JsonNode.Parse(document)!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>The members of a heavy leg's entry a build before the queue's own record reads, every one of them and no more.</summary>
    private sealed record OlderSlotEntry(
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
        string? Variant = null);

    /// <summary>
    /// A machine of a test's own: its dispatch queue over a record in a directory the test removes, the machine's own count
    /// of slots where it declares one, and a clock the test moves.
    /// </summary>
    private sealed class Machine : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public Machine(int? slots)
        {
            Record = _temp.Combine("admission.json");
            Queue = new MachineDispatchQueue(Harness.FileSystem, Harness.Output, Harness.Identity, Record, () => slots, clock: Clock);
        }

        public HarnessFactory Harness { get; } = new();

        public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));

        public string Record { get; }

        public MachineDispatchQueue Queue { get; }

        /// <summary>A unit of <paramref name="repository"/> asking for a slot.</summary>
        public SlotPlace Ask(string leg, string repository, int slots = 2, int priority = 1, double weight = 1, TimeSpan? ageAfter = null, string command = "build")
            => Queue.Ask($"run-{leg}", command, leg, "local", repository, null, slots, null, repository, priority, ageAfter, weight);

        /// <summary>
        /// A unit of <paramref name="leg"/> that ran on this machine before the scene begins, its duration kept as the
        /// queue itself keeps one (<see cref="UnitDurations.Record"/>): what the next unit of its name is charged. Its
        /// tenant's charge for it is not part of the scene - a past run's debt was paid back as other tenants were served.
        /// </summary>
        public void RanBefore(string repository, string leg, TimeSpan held, string command = "build")
            => new UnitDurations(Harness.FileSystem, Harness.Output, MachineDispatchQueue.DurationsPathFor(Record))
                .Record(repository, command, leg, held, Clock.GetUtcNow());

        /// <summary>A unit of <paramref name="leg"/> asked, granted and run for <paramref name="held"/>, its run kept as the queue keeps it; a minute later.</summary>
        public void Measure(string repository, string leg, TimeSpan held, string command = "build")
        {
            var place = Ask(leg, repository, command: command);

            Assert.True(Queue.Look(place).Holding);
            place.Ran(held);
            Clock.Advance(TimeSpan.FromMinutes(1));
        }

        /// <summary>
        /// The units of <paramref name="waiting"/> in the order the queue grants them its slots: each look grants what is free,
        /// and each unit granted gives its slot back before the next look - <paramref name="held"/> later on the clock, where
        /// given, so the units still waiting age as the granted ones run.
        /// </summary>
        public IReadOnlyList<string> Starts(List<SlotPlace> waiting, TimeSpan? held = null)
        {
            var starts = new List<string>();

            while (waiting.Count > 0)
            {
                Queue.Look(waiting[0]);

                var granted = waiting.Where(place => Queue.Look(place).Holding).ToList();

                Assert.NotEmpty(granted);
                Clock.Advance(held ?? TimeSpan.Zero);

                foreach (var place in granted)
                {
                    starts.Add(place.Entry.Leg);
                    waiting.Remove(place);
                    place.Dispose();
                }
            }

            return starts;
        }

        public void Dispose() => _temp.Dispose();
    }

    /// <summary>Processes of one machine as a test says they stand: this one, and others it names, alive until it ends them.</summary>
    private sealed class Processes(IProcessIdentity real)
    {
        private readonly HashSet<int> _ended = [];

        /// <summary>The identity of process <paramref name="processId"/> of this machine, telling the others' liveness as this test says.</summary>
        public IProcessIdentity As(int processId) => new Identity(this, real, processId);

        public void End(int processId) => _ended.Add(processId);

        private sealed class Identity(Processes processes, IProcessIdentity real, int processId) : IProcessIdentity
        {
            public int CurrentId => processId;

            public string CurrentMachine => real.CurrentMachine;

            public string? Current => processId == real.CurrentId ? real.Current : null;

            public bool IsAlive(int id, string? stamp) => id == real.CurrentId ? real.IsAlive(id, stamp) : !processes._ended.Contains(id);

            public string? StampOf(int id) => id == real.CurrentId ? real.StampOf(id) : null;

            public bool IsSame(int id, string stamp) => id == real.CurrentId && real.IsSame(id, stamp);
        }
    }
}
