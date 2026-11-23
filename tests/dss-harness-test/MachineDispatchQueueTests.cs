using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// The machine's dispatch queue: fair by tenant - of the units waiting, the one whose tenant is charged the least
/// against its share goes next, ties by arrival - with weights, priorities and aging each multiplying the share, the
/// slots' own room rules kept whole, and what a unit costs read from the records the tool keeps. Nothing pre-empted;
/// a dead holder released.
/// </summary>
public sealed class MachineDispatchQueueTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static SlotEntry Entry(string leg, string tenant, double cost = 1, double weight = 1, int priority = 1, int minutesWaited = 0, double ageAfter = 10, int slots = 2)
        => new("machine", 1, "r1", Now - TimeSpan.FromMinutes(minutesWaited), "build", leg, "local", $"/trees/{tenant}", slots)
        {
            Tenant = tenant,
            Weight = weight,
            Priority = priority,
            Cost = cost,
            AgeAfterMinutes = ageAfter,
        };

    private static MachineDispatchQueue Queue()
        => new(
            new HarnessFactory().FileSystem,
            new HarnessFactory().Output,
            new HarnessFactory().Identity,
            () => Path.Combine(Path.GetTempPath(), $"admission-tests-{Guid.NewGuid():N}.json"));

    /// <summary>A tenant with a burst of short arms does not hold the machine against another's single long build.</summary>
    [Fact]
    public void TwoTenantsOfUnequalBursts_EachGetsTheMachineInProportion()
    {
        var queue = Queue();

        // Tenant A asks for five cheap arms; tenant B, after three of them, for one longer build.
        var line = new List<SlotEntry>
        {
            Entry("arm-1", "a", cost: 1, minutesWaited: 9),
            Entry("arm-2", "a", cost: 1, minutesWaited: 8),
            Entry("arm-3", "a", cost: 1, minutesWaited: 7),
            Entry("build", "b", cost: 4, minutesWaited: 6),
            Entry("arm-4", "a", cost: 1, minutesWaited: 5),
            Entry("arm-5", "a", cost: 1, minutesWaited: 4),
        };

        // Two slots: the first arm by arrival, then - its tenant charged one against B's nothing - B's build,
        // before A's second arm.
        var holding = queue.HoldingOf(line, machine: 2, Now);

        Assert.Equal(["arm-1", "build"], holding.Select(entry => entry.Leg));
    }

    /// <summary>A heavier tenant is served more of the machine in proportion to its weight, not by waiting less.</summary>
    [Fact]
    public void AHeavierTenant_ShareGrowsByItsWeight()
    {
        var queue = Queue();

        var line = new List<SlotEntry>
        {
            Entry("light-1", "light", cost: 2, minutesWaited: 3),
            Entry("heavy-1", "heavy", cost: 2, weight: 2, minutesWaited: 2),
            Entry("light-2", "light", cost: 2, minutesWaited: 1),
            Entry("heavy-2", "heavy", cost: 2, weight: 2, minutesWaited: 0),
        };

        var holding = queue.HoldingOf(line, machine: 4, Now);

        // The first two by arrival (no charge yet); then the weight-2 tenant's second costs 1 against its share
        // where the weight-1 tenant's costs 2 against its own.
        Assert.Equal(["light-1", "heavy-1", "heavy-2", "light-2"], holding.Select(entry => entry.Leg));
    }

    /// <summary>A run the workflow marked with a higher priority is served more of the machine, as a weight is.</summary>
    [Fact]
    public void AHigherPriorityRun_IsServedMoreOfTheMachine()
    {
        var queue = Queue();

        var line = new List<SlotEntry>
        {
            Entry("later-1", "x", cost: 2, minutesWaited: 3),
            Entry("urgent-1", "y", cost: 2, priority: 2, minutesWaited: 2),
            Entry("later-2", "x", cost: 2, minutesWaited: 1),
            Entry("urgent-2", "y", cost: 2, priority: 2, minutesWaited: 0),
        };

        var holding = queue.HoldingOf(line, machine: 4, Now);

        Assert.Equal(["later-1", "urgent-1", "urgent-2", "later-2"], holding.Select(entry => entry.Leg));
    }

    /// <summary>A unit waiting past the aging bound has its tenant's share doubled for each further period, so no weight setting starves a tenant.</summary>
    [Fact]
    public void AUnitWaitingPastTheAgingBound_GetsItsTenantsShareDoubled()
    {
        var queue = Queue();

        // Both tenants hold first (by arrival); then light's second unit has cost 4 against a share of 1, where
        // starved's second has cost 8 against a share of 4 - two aging periods of 10 for its 24 minutes of waiting -
        // and so goes first: without the aging, 8 against 4 would leave it last.
        var line = new List<SlotEntry>
        {
            Entry("rich-1", "rich", cost: 4, minutesWaited: 30),
            Entry("starved-1", "starved", cost: 8, minutesWaited: 25),
            Entry("light-2", "rich", cost: 4, minutesWaited: 5),
            Entry("starved-2", "starved", cost: 8, minutesWaited: 24),
        };

        var holding = queue.HoldingOf(line, machine: 4, Now);

        Assert.Equal(["rich-1", "starved-1", "starved-2", "light-2"], holding.Select(entry => entry.Leg));
    }

    /// <summary>The slots' own room rule is kept whole: no unit runs beside more than the fewest any holder or it allows.</summary>
    [Fact]
    public void TheSlotsOwnRoomRule_KeptWhole()
    {
        var queue = Queue();

        // The first entry allows only one at once; the second, two: nothing runs beside the first whatever the order.
        var line = new List<SlotEntry>
        {
            Entry("lone", "a", slots: 1),
            Entry("pair-1", "b", slots: 2),
            Entry("pair-2", "c", slots: 2),
        };

        var holding = queue.HoldingOf(line, machine: null, Now);

        Assert.Equal(["lone"], holding.Select(entry => entry.Leg));
    }

    /// <summary>What a unit costs is the median of the last ten durations the tree's ledgers hold for the same leg; one quantum where none is known.</summary>
    [Fact]
    public void TheCost_IsTheMedianOfTheLastTenDurations_OfTheSameLeg()
    {
        using var temp = new TempDirectory();
        var steps = temp.Combine(".orchestrators", "r1", "steps", "items[0].build");
        Directory.CreateDirectory(steps);
        File.WriteAllText(
            Path.Combine(steps, "ledger.json"),
            """{"legs":[{"leg":"linux-release","duration":"00:01:00"},{"leg":"linux-release","duration":"00:03:00"},{"leg":"linux-release","duration":"00:09:00"},{"leg":"win-release","duration":"00:05:00"}]}""");

        var fileSystem = new HarnessFactory().FileSystem;

        Assert.Equal(3, DispatchCosts.Of(fileSystem, temp.Path, "linux-release"));
        Assert.Equal(5, DispatchCosts.Of(fileSystem, temp.Path, "win-release"));
        Assert.Equal(1, DispatchCosts.Of(fileSystem, temp.Path, "unknown-leg"));
        Assert.Equal(1, DispatchCosts.Of(fileSystem, temp.Combine("no-orchestrators-here"), "linux-release"));
    }

    /// <summary>The queue command answers each class's capacity, holders and waiting units, by tenant, in text and as one JSON document.</summary>
    [Fact]
    public void TheQueueCommand_AnswersEachClass_ByTenant()
    {
        var queue = Queue();
        var place = queue.Ask("r1", "build", "linux-release", "local", "/trees/one", null, 2, tenant: "local//trees/one", cost: 3);

        var classes = queue.Queue();
        var heavy = classes.Single(@class => @class.Class == "heavy");

        Assert.Equal(2, heavy.Capacity);
        var holder = Assert.Single(heavy.Holders);

        Assert.Equal("local//trees/one", holder.Tenant);
        Assert.Equal(3, holder.Cost);

        place.Dispose();
    }

    /// <summary>A waiting leg's line names its tenant, so a reader sees who the queue serves.</summary>
    [Fact]
    public async Task AWaitingLegsLine_NamesItsTenant()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "theirs"));

        var said = new List<string>();
        var admission = AdmissionKit.Admission(harness, record, new ScriptedGauge(30), new ManualClock());

        // One slot, held by another: the wait says who holds it - and whose tenant this leg waits as.
        using var place = await admission.AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 1), said), TestContext.Current.CancellationToken);

        Assert.Contains(said, line => line.Contains("as tenant 'local/", StringComparison.Ordinal));
    }
}
