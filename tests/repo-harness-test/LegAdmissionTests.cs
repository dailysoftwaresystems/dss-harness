using RepoHarness.Core.Execution;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// A heavy leg waits for its machine to take it: one of the machine's slots, in the order legs asked, then the memory
/// in use below the limit - read again after a settle where another leg holds a slot. Four worktrees' builds on one machine
/// drove its committed memory to 81 of 113.7 GB and the process that started them died; the limit is the consumer's
/// own rule, and a slot is held by the process running the leg, so one that crashed never keeps it.
/// </summary>
public sealed class LegAdmissionTests
{
    /// <summary>A machine nothing else builds on takes the leg at once, and its line names the memory it started at.</summary>
    [Fact]
    public async Task AnIdleMachine_TakesALegAtOnce_NamingTheMemoryItStartedAt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var ledger = temp.Combine("state", "admission.json");
        var clock = new ManualClock();
        var said = new List<string>();

        using (var admitted = await AdmissionKit.Admission(harness, ledger, new ScriptedGauge(41.25), clock)
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(), said), TestContext.Current.CancellationToken))
        {
            Assert.Null(admitted.Refusal);
            Assert.True(admitted.Fact.Admitted);
            Assert.Equal(0, admitted.Fact.WaitedSeconds);
            Assert.Equal(41.3, admitted.Fact.MemoryPercent);
            Assert.Equal("admitted at once, memory 41.3% in use (41.25 of 100 by the test)", admitted.Fact.Describe());
            Assert.Equal("mine", Assert.Single(AdmissionKit.Read(ledger)).Leg);
        }

        // Given back as the leg's work ends, and the settle skipped: nothing else held a slot.
        Assert.Empty(AdmissionKit.Read(ledger));
        Assert.Equal(TimeSpan.Zero, clock.Moved);
    }

    /// <summary>
    /// With every slot held the leg waits its turn, saying who holds each - tree, variant, leg, process, run, since - and
    /// is taken once one of them has given its slot back.
    /// </summary>
    [Fact]
    public async Task ALegWaitsItsTurnForASlot_NamingWhoHoldsThem()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var ledger = temp.Combine("admission.json");
        var clock = new ManualClock();
        var said = new List<string>();
        var first = AdmissionKit.Holder(harness, "first");
        var second = AdmissionKit.Holder(harness, "second");

        AdmissionKit.Write(ledger, first, second);

        // The first holder's command ends while this leg waits its second poll.
        var waits = 0;
        var admission = AdmissionKit.Admission(harness, ledger, new ScriptedGauge(30), clock, onWait: () =>
        {
            if (++waits == 2)
            {
                AdmissionKit.Write(ledger, [.. AdmissionKit.Read(ledger).Where(entry => entry.Leg != "first")]);
            }
        });

        // No settle: what the settle is for is the next test's.
        using var admitted = await admission.AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 2, settleLeast: 0, settleMost: 0), said), TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(60, admitted.Fact.WaitedSeconds);

        var waiting = Assert.Single(said, line => line.StartsWith("waits for", StringComparison.Ordinal));

        Assert.Contains("one of this machine's 2 heavy-leg slot(s), 2 leg(s) ahead", waiting, StringComparison.Ordinal);
        Assert.Contains($"'/src/first' variant 'x86_64-gcc-release' (leg 'first', test, {harness.Identity.CurrentMachine} pid {harness.Identity.CurrentId}, run run-first, since 2026-09-30 16:29:42Z)", waiting, StringComparison.Ordinal);
        Assert.Contains("'/src/second'", waiting, StringComparison.Ordinal);

        // Taken after the other two, and holding one of the two slots with the second.
        Assert.Equal(["second", "mine"], AdmissionKit.Read(ledger).Select(entry => entry.Leg));
    }

    /// <summary>
    /// A leg that waited as long as its machine allows is not taken: its line names what held the slots, it gives its
    /// place back, and nothing of it runs. Never failed: the machine had no room, which says nothing of the code.
    /// </summary>
    [Fact]
    public async Task ALegThatWaitedAsLongAsItsMachineAllows_IsNotTaken_NamingWhatHeldTheSlots()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var ledger = temp.Combine("admission.json");
        var clock = new ManualClock();
        var said = new List<string>();

        AdmissionKit.Write(ledger, AdmissionKit.Holder(harness, "first"), AdmissionKit.Holder(harness, "second"));

        using var admitted = await AdmissionKit.Admission(harness, ledger, new ScriptedGauge(30), clock)
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(pollSeconds: 30, maxWaitMinutes: 2), said), TestContext.Current.CancellationToken);

        Assert.False(admitted.Fact.Admitted);
        Assert.Equal(120, admitted.Fact.WaitedSeconds);
        Assert.StartsWith("not admitted after 2m00s waiting for one of this machine's 2 heavy-leg slot(s), held by '/src/first'", admitted.Refusal, StringComparison.Ordinal);
        Assert.Equal(2, admitted.Fact.Holders?.Count);
        Assert.Equal(["first", "second"], AdmissionKit.Read(ledger).Select(entry => entry.Leg));
    }

    /// <summary>
    /// Holding a slot, a leg waits for the memory in use to fall below the limit, saying so once, and starts at the
    /// reading that was below it.
    /// </summary>
    [Fact]
    public async Task ALegHoldingASlot_WaitsForTheMemoryToFallBelowTheLimit()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var ledger = temp.Combine("admission.json");
        var clock = new ManualClock();
        var said = new List<string>();

        using var admitted = await AdmissionKit.Admission(harness, ledger, new ScriptedGauge(84.1, 80, 75.9), clock)
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(pollSeconds: 30), said), TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(60, admitted.Fact.WaitedSeconds);
        Assert.Equal(75.9, admitted.Fact.MemoryPercent);
        Assert.Equal(
            "holds a heavy-leg slot, and waits for the memory 84.1% in use (84.1 of 100 by the test) to fall below 76%",
            Assert.Single(said, line => line.StartsWith("holds", StringComparison.Ordinal)));
        Assert.Equal("admitted after 1m00s, memory 75.9% in use (75.9 of 100 by the test)", said[^1]);
    }

    /// <summary>
    /// Where another leg holds a slot, a reading below the limit is read again after a settle, and the leg starts only
    /// if it still is: two legs taking their slots together would otherwise both start on one reading. One whose
    /// reading rose during the settle goes on waiting.
    /// </summary>
    [Fact]
    public async Task WhereAnotherLegHoldsASlot_ALegStartsOnlyIfTheMemoryIsStillBelowAfterASettle()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var ledger = temp.Combine("admission.json");
        var clock = new ManualClock();
        var said = new List<string>();

        AdmissionKit.Write(ledger, AdmissionKit.Holder(harness, "neighbour"));

        // Below, then above once the neighbour's build has grown, then below twice.
        var gauge = new ScriptedGauge(60, 79, 70, 71);

        using var admitted = await AdmissionKit.Admission(harness, ledger, gauge, clock, settle: TimeSpan.FromSeconds(20))
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(pollSeconds: 30), said), TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(71, admitted.Fact.MemoryPercent);
        Assert.Equal(4, gauge.Reads);

        // Settled twice, and polled once between: the first settle's second reading was above the limit.
        Assert.Equal(TimeSpan.FromSeconds(20 + 30 + 20), clock.Moved);
        Assert.Equal(2, said.Count(line => line.Contains("another leg holds a slot, so it looks again in 20s", StringComparison.Ordinal)));
    }

    /// <summary>A leg whose memory never falls below the limit is not taken, naming the reading and the limit.</summary>
    [Fact]
    public async Task ALegWhoseMemoryNeverFalls_IsNotTaken_NamingTheReadingAndTheLimit()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var ledger = temp.Combine("admission.json");
        var said = new List<string>();

        using var admitted = await AdmissionKit.Admission(harness, ledger, new ScriptedGauge(90), new ManualClock())
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(pollSeconds: 60, maxWaitMinutes: 3), said), TestContext.Current.CancellationToken);

        Assert.False(admitted.Fact.Admitted);
        Assert.Equal(90, admitted.Fact.MemoryPercent);
        Assert.Null(admitted.Fact.Holders);
        Assert.Equal(
            "not admitted after 3m00s: it held a heavy-leg slot, and the memory 90.0% in use (90 of 100 by the test) never fell below 76%",
            admitted.Refusal);
        Assert.Empty(AdmissionKit.Read(ledger));
    }

    /// <summary>
    /// A machine whose memory cannot be read takes the leg on its slot alone, and its line says so: refused instead, a
    /// machine whose count could not be read would never take a heavy leg.
    /// </summary>
    [Fact]
    public async Task AMachineWhoseMemoryCannotBeRead_TakesTheLegOnItsSlotAlone_SayingSo()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var said = new List<string>();

        using var admitted = await AdmissionKit.Admission(harness, temp.Combine("admission.json"), new ScriptedGauge([null]), new ManualClock())
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(), said), TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Null(admitted.Fact.MemoryPercent);
        Assert.Equal("the test gave no reading", admitted.Fact.Unmeasured);
        Assert.Equal("admitted at once on its slot alone, the memory in use unread: the test gave no reading", admitted.Fact.Describe());
    }

    /// <summary>
    /// A slot held by a command that has ended - crashed, or killed - is reclaimed by the next leg that looks, and said
    /// to be; one whose command still runs holds its slot whatever name the machine had when it asked, since a Mac
    /// takes its name from each network it joins, and read by name a renamed machine's slots would all be free.
    /// </summary>
    [Fact]
    public async Task ASlotWhoseCommandHasEnded_IsReclaimed_AndOneStillRunningUnderAnOldNameIsNot()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var ledger = temp.Combine("admission.json");

        AdmissionKit.Write(
            ledger,
            AdmissionKit.Holder(harness, "crashed", processId: int.MaxValue - 1),
            AdmissionKit.Holder(harness, "renamed", machine: "the-name-it-had"));

        using var admitted = await AdmissionKit.Admission(harness, ledger, new ScriptedGauge(10), new ManualClock())
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 1, maxWaitMinutes: 1), []), TestContext.Current.CancellationToken);

        Assert.False(admitted.Fact.Admitted);
        Assert.Contains("held by '/src/renamed'", admitted.Refusal, StringComparison.Ordinal);
        Assert.Equal(["renamed"], AdmissionKit.Read(ledger).Select(entry => entry.Leg));
        Assert.Contains("Reclaimed a heavy-leg slot from '/src/crashed'", harness.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("which is no longer running", harness.StandardOutput.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg holding a slot and waiting for the memory whose place went - its record removed by hand - waits its turn
    /// again, rather than starting on a slot it no longer holds.
    /// </summary>
    [Fact]
    public async Task ALegWhosePlaceWentWhileItWaitedForTheMemory_WaitsItsTurnAgain()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var ledger = temp.Combine("admission.json");
        var said = new List<string>();
        var waits = 0;

        // While it waits for the memory, its record is removed and another leg takes the machine's one slot.
        var admission = AdmissionKit.Admission(harness, ledger, new ScriptedGauge(90, 10), new ManualClock(), onWait: () =>
        {
            if (++waits == 1)
            {
                AdmissionKit.Write(ledger, AdmissionKit.Holder(harness, "newcomer"));
            }
        });

        using var admitted = await admission.AdmitAsync(
            AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 1, maxWaitMinutes: 2), said),
            TestContext.Current.CancellationToken);

        Assert.False(admitted.Fact.Admitted);
        Assert.StartsWith("not admitted after 2m00s waiting for one of this machine's 1 heavy-leg slot(s), held by '/src/newcomer'", admitted.Refusal, StringComparison.Ordinal);
        Assert.Contains(said, line => line.StartsWith("holds a heavy-leg slot", StringComparison.Ordinal));
        Assert.Contains(said, line => line.StartsWith("waits for one of this machine's 1 heavy-leg slot(s), 1 leg(s) ahead", StringComparison.Ordinal));
    }

    /// <summary>
    /// Each machine keeps its own record, named by what tells it from every other rather than by its name, so a home
    /// two machines share holds one for each, and neither's change can lose an entry the other wrote.
    /// </summary>
    [Fact]
    public void EachMachine_KeepsARecordOfItsOwn_NamedByWhatTellsItApart()
    {
        Assert.Equal("admission-4c4c4544-0036-3510-8052-b4c04f4d4d32.json", HeavyLegSlots.FileNameFor("4c4c4544-0036-3510-8052-b4c04f4d4d32"));
        Assert.Equal("admission-a_b_c.json", HeavyLegSlots.FileNameFor("a/b\\c"));

        var machine = new HarnessFactory().Platform.MachineId;

        Assert.Matches("^[0-9A-Fa-f-]{32,36}$", machine);
        Assert.Equal(machine, new HarnessFactory().Platform.MachineId);
    }

    /// <summary>A leg stopped while it waits gives its place back, and says no verdict.</summary>
    [Fact]
    public async Task ALegStoppedWhileItWaits_GivesItsPlaceBack()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var ledger = temp.Combine("admission.json");
        using var stop = new CancellationTokenSource();

        AdmissionKit.Write(ledger, AdmissionKit.Holder(harness, "first"));

        var admission = AdmissionKit.Admission(harness, ledger, new ScriptedGauge(10), new ManualClock(), onWait: stop.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var admitted = await admission.AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 1), []), stop.Token);
        });

        Assert.Equal(["first"], AdmissionKit.Read(ledger).Select(entry => entry.Leg));
    }

    /// <summary>
    /// A process that can name no directory of its user's own keeps no slots anywhere else - never in the directory every
    /// user shares - and refuses its heavy legs, saying why.
    /// </summary>
    [Fact]
    public async Task ARecordWithNowhereToBeKept_RefusesTheLeg_NeverKeptWhereEveryUserCanWrite()
    {
        var harness = new HarnessFactory();
        var slots = new HeavyLegSlots(
            harness.FileSystem,
            harness.Output,
            harness.Identity,
            () => throw new DirectoryNotFoundException("the process was given no home"));
        var admission = new LegAdmission(slots, new ScriptedGauge(10), new ManualClock(), (_, _) => Task.CompletedTask, (least, _) => least);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => admission.AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(), []), TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Equal(
            "The record of the heavy legs admitted onto this machine has nowhere to be kept: the process was given no home. "
            + "Until it can be, no heavy leg is admitted onto this machine.",
            refusal.Message);
    }

    /// <summary>
    /// A record that cannot be read is never read as free - that is the one reading that would start every waiting
    /// leg at once - and is refused, naming it and what to do.
    /// </summary>
    [Fact]
    public async Task ARecordThatCannotBeRead_IsRefused_NeverReadAsFree()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var ledger = temp.Combine("admission.json");

        File.WriteAllText(ledger, "not a record");

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => AdmissionKit.Admission(harness, ledger, new ScriptedGauge(10), new ManualClock())
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(), []), TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains($"The record of the heavy legs admitted onto this machine '{Path.GetFullPath(ledger)}' is not readable as JSON", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Remove it once no heavy leg runs or waits on this machine.", refusal.Message, StringComparison.Ordinal);
    }
}
