using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

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

    /// <summary>
    /// What a unit costs is the median of the newest ten durations the tree's ledgers hold for the same leg - newest by
    /// the run each was of - read from every run's step ledgers, its agents' steps' among them, as the step kept the
    /// leg run's own ledger document; a leg that never ran is no duration, a ledger that cannot be read holds none, and
    /// one quantum is the cost where none is known.
    /// </summary>
    [Fact]
    public void TheCost_IsTheMedianOfTheNewestTenDurations_TheLedgersHoldForTheSameLeg()
    {
        using var temp = new TempDirectory();

        // Twelve runs of the leg in the order they began, the two oldest the longest (30 and 20 minutes), the rest 3 to 12,
        // the newest four kept by an agent's step of the last of twenty-one runs: the newest ten are 3 to 12, whose median
        // is 7.5 - where every run counted, or the ten longest were taken, or the agents' steps or any run were passed
        // over, it would be 8.5, 9.5 or 6.5.
        for (var run = 1; run <= 12; run++)
        {
            var steps = run <= 8
                ? temp.Combine(".orchestrators", $"o{run:00}", "steps", $"items[{run}].build")
                : temp.Combine(".orchestrators", "o21", "agents", "a1", "steps", $"items[{run}].build");

            Kept(steps, Run(run), ("linux-release", TimeSpan.FromMinutes(run switch { 1 => 30, 2 => 20, _ => run })), ("win-release", TimeSpan.Zero));
        }

        for (var run = 9; run <= 20; run++)
        {
            Directory.CreateDirectory(temp.Combine(".orchestrators", $"o{run:00}", "steps"));
        }

        Kept(temp.Combine(".orchestrators", "o01", "steps", "items[0].build"), Run(13), ("win-release", TimeSpan.FromMinutes(5)));
        File.WriteAllText(temp.Combine(".orchestrators", "o01", "steps", "items[0].build", "nested", "ledger.json"), "not a ledger");
        var fileSystem = new HarnessFactory().FileSystem;

        Assert.Equal(7.5, DispatchCosts.Of(fileSystem, temp.Path, "linux-release"));
        Assert.Equal(5, DispatchCosts.Of(fileSystem, temp.Path, "win-release"));
        Assert.Equal(1, DispatchCosts.Of(fileSystem, temp.Path, "unknown-leg"));
        Assert.Equal(1, DispatchCosts.Of(fileSystem, temp.Combine("no-orchestrators-here"), "linux-release"));

        // The run a ledger is of: one begun on the given minute of a day, so the runs' order is the minutes'.
        static RunId Run(int minute) => RunId.Parse($"20261123-{(minute / 60):00}{(minute % 60):00}00-0000000a");

        // A step's ledger, as the step keeps it: the leg run's own document, written by the ledger's own writer.
        static void Kept(string steps, RunId run, params (string Leg, TimeSpan Took)[] legs)
        {
            Directory.CreateDirectory(Path.Combine(steps, "nested"));
            var result = new DssHarness.Core.Runs.LegRunResult(
                "build",
                run,
                "/runs/r",
                LedgerReport.From([.. legs.Select(leg => new LegEntry { Leg = leg.Leg, Verdict = LegVerdict.Passed, Duration = leg.Took })], 0),
                HarnessExit.Success,
                "passed",
                Cancelled: false,
                Unfinished: [],
                Details: []);

            File.WriteAllText(Path.Combine(steps, "ledger.json"), result.ToDocument(path => path) + "\n");
        }
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

    /// <summary>
    /// The queue command's answer: asked for <c>--json</c>, its one document whole - every class with its capacity, its
    /// holders and its waiting units - after the code and the line it ends on, and nothing else on standard output; as
    /// text, a listing of a line a unit under its class, the counts the line it ends on. The stopped document is the same
    /// shape, holding no classes.
    /// </summary>
    [Fact]
    public void TheQueueCommandsAnswer_IsItsDocumentWhole_WhereAsked_ElseItsListing()
    {
        IReadOnlyList<DispatchClass> classes =
        [
            new("heavy", 2, [new DispatchUnit("local/a", 1, 1, 4, null, "tree a")], [new DispatchUnit("local/b", 2, 3, 1.5, TimeSpan.FromSeconds(185), "tree b")]),
            new("agent", 8, [], []),
        ];
        const string Summary = "1 unit(s) holding and 1 unit(s) waiting, in 2 class(es)";

        var json = QueueReports.Render(classes, json: true);
        var text = QueueReports.Render(classes, json: false);

        Assert.Equal((HarnessExit.Success, Summary, true), (json.ExitCode, json.Message, json.Quiet));
        Assert.Equal(
            """{"exitCode":0,"summary":"1 unit(s) holding and 1 unit(s) waiting, in 2 class(es)","queue":[{"class":"heavy","capacity":2,"holding":[{"tenant":"local/a","weight":1,"priority":1,"cost":4,"waited":null,"unit":"tree a"}],"waiting":[{"tenant":"local/b","weight":2,"priority":3,"cost":1.5,"waited":"3m05s","unit":"tree b"}]},{"class":"agent","capacity":8,"holding":[],"waiting":[]}]}""",
            System.Text.Json.Nodes.JsonNode.Parse(Assert.Single(json.Data))!.ToJsonString());
        Assert.Equal((HarnessExit.Success, Summary, false), (text.ExitCode, text.Message, text.Quiet));
        Assert.Equal(
            [
                "heavy: 2 unit(s) at once",
                "  holding (tenant 'local/a'): tree a",
                "  waiting (tenant 'local/b', cost 1.5, weight 2, priority 3, waited 3m05s): tree b",
                "agent: 8 unit(s) at once",
            ],
            text.Data);
        Assert.Equal(
            """{"exitCode":13,"summary":"stopped","queue":null}""",
            System.Text.Json.Nodes.JsonNode.Parse(QueueReports.Stopped(HarnessExit.Refused, "stopped"))!.ToJsonString());
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
