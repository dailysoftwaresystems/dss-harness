using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// A heavy leg waits for its machine to take it: one of the machine's slots, in the order legs asked, then the memory
/// in use below the limit - read again after a settle where another leg holds a slot. Four worktrees' builds on one machine
/// drove its committed memory to 81 of 113.7 GiB and the process that started them died; the limit of 76% is the one
/// the consumer's own scripts kept, and a slot is held by the process that asked for it, so one that crashed never keeps it.
/// </summary>
public sealed class LegAdmissionTests
{
    /// <summary>A machine nothing else builds on takes the leg at once, and its line names the memory it started at.</summary>
    [Fact]
    public async Task AnIdleMachine_TakesALegAtOnce_NamingTheMemoryItStartedAt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("state", "admission.json");
        var clock = new ManualClock();
        var said = new List<string>();

        using (var admitted = await AdmissionKit.Admission(harness, record, new ScriptedGauge(41.25), clock)
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(), said), TestContext.Current.CancellationToken))
        {
            Assert.Null(admitted.Refusal);
            Assert.True(admitted.Fact.Admitted);
            Assert.Equal(0, admitted.Fact.WaitedSeconds);
            Assert.Equal(41.3, admitted.Fact.MemoryPercent);
            Assert.Equal("admitted at once, memory 41.3% in use (41.25 of 100 by the test)", admitted.Fact.Describe());
            Assert.Equal("mine", Assert.Single(AdmissionKit.Read(record)).Leg);
        }

        // Given back as the leg's work ends, and the settle skipped: nothing else held a slot.
        Assert.Empty(AdmissionKit.Read(record));
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
        var record = temp.Combine("admission.json");
        var clock = new ManualClock();
        var said = new List<string>();
        var first = AdmissionKit.Holder(harness, "first");
        var second = AdmissionKit.Holder(harness, "second");

        AdmissionKit.Write(record, first, second);

        // The first holder gives its slot back while this leg waits its second poll.
        var waits = 0;
        var admission = AdmissionKit.Admission(harness, record, new ScriptedGauge(30), clock, onWait: () =>
        {
            if (++waits == 2)
            {
                AdmissionKit.Write(record, [.. AdmissionKit.Read(record).Where(entry => entry.Leg != "first")]);
            }
        });

        // No settle: what the settle is for is WhereAnotherLegHoldsASlot_ALegStartsOnlyIfTheMemoryIsStillBelowAfterASettle's.
        using var admitted = await admission.AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 2, settleLeast: 0, settleMost: 0), said), TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(60, admitted.Fact.WaitedSeconds);

        var waiting = Assert.Single(said, line => line.StartsWith("waits for", StringComparison.Ordinal));

        Assert.Contains("one of this machine's 2 heavy-leg slot(s), 2 leg(s) ahead", waiting, StringComparison.Ordinal);
        Assert.Contains($"'/src/first' variant 'x86_64-gcc-release' on local (leg 'first', test, {harness.Identity.CurrentMachine} pid {harness.Identity.CurrentId}, run run-first, since 2026-09-30 16:29:42Z)", waiting, StringComparison.Ordinal);
        Assert.Contains("'/src/second'", waiting, StringComparison.Ordinal);

        // Taken after the other two, and holding one of the two slots with the second.
        Assert.Equal(["second", "mine"], AdmissionKit.Read(record).Select(entry => entry.Leg));
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
        var record = temp.Combine("admission.json");
        var clock = new ManualClock();
        var said = new List<string>();

        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "first"), AdmissionKit.Holder(harness, "second"));

        using var admitted = await AdmissionKit.Admission(harness, record, new ScriptedGauge(30), clock)
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(pollSeconds: 30, maxWaitMinutes: 2), said), TestContext.Current.CancellationToken);

        Assert.False(admitted.Fact.Admitted);
        Assert.Equal(120, admitted.Fact.WaitedSeconds);
        Assert.StartsWith("not admitted after 2m00s waiting for one of this machine's 2 heavy-leg slot(s), held by '/src/first'", admitted.Refusal, StringComparison.Ordinal);
        Assert.EndsWith($"; the machine's heavy legs are recorded in '{record}'", admitted.Refusal, StringComparison.Ordinal);
        Assert.Equal(record, admitted.Fact.Record);
        Assert.Equal(2, admitted.Fact.Holders?.Count);
        Assert.Equal(["first", "second"], AdmissionKit.Read(record).Select(entry => entry.Leg));
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
        var record = temp.Combine("admission.json");
        var clock = new ManualClock();
        var said = new List<string>();

        using var admitted = await AdmissionKit.Admission(harness, record, new ScriptedGauge(84.1, 80, 75.9), clock)
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
        var record = temp.Combine("admission.json");
        var clock = new ManualClock();
        var said = new List<string>();

        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "neighbour"));

        // Below, then above once the neighbour's build has grown, then below twice.
        var gauge = new ScriptedGauge(60, 79, 70, 71);

        using var admitted = await AdmissionKit.Admission(harness, record, gauge, clock, settle: TimeSpan.FromSeconds(20))
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(pollSeconds: 30), said), TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(71, admitted.Fact.MemoryPercent);
        Assert.Equal(4, gauge.Reads);

        // Settled twice, and polled once between: the first settle's second reading was above the limit.
        Assert.Equal(TimeSpan.FromSeconds(20 + 30 + 20), clock.Moved);
        Assert.Equal(2, said.Count(line => line.Contains("another leg holds a slot, so it looks again in 20s", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A unit asking not to settle - an arm of a sweep its machine already took once - starts on its first reading below
    /// the limit, whoever holds a slot beside it: the slots it would wait out are its own sweep's. One above the limit
    /// still waits for the memory, as any leg does.
    /// </summary>
    [Fact]
    public async Task AUnitAskingNotToSettle_StartsOnItsFirstReadingBelowTheLimit()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var clock = new ManualClock();
        var said = new List<string>();

        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "mine/first-arm"));

        var gauge = new ScriptedGauge(79, 60);

        using var admitted = await AdmissionKit.Admission(harness, record, gauge, clock, settle: TimeSpan.FromSeconds(20))
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 2, pollSeconds: 30), said) with { Settle = false }, TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(60, admitted.Fact.MemoryPercent);
        Assert.Equal(2, gauge.Reads);
        Assert.Equal(TimeSpan.FromSeconds(30), clock.Moved);
        Assert.DoesNotContain(said, line => line.Contains("looks again in", StringComparison.Ordinal));
    }

    /// <summary>A leg whose memory never falls below the limit is not taken, naming the reading and the limit.</summary>
    [Fact]
    public async Task ALegWhoseMemoryNeverFalls_IsNotTaken_NamingTheReadingAndTheLimit()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var said = new List<string>();

        using var admitted = await AdmissionKit.Admission(harness, record, new ScriptedGauge(90), new ManualClock())
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(pollSeconds: 60, maxWaitMinutes: 3), said), TestContext.Current.CancellationToken);

        Assert.False(admitted.Fact.Admitted);
        Assert.Equal(90, admitted.Fact.MemoryPercent);
        Assert.Null(admitted.Fact.Holders);
        Assert.Equal(
            "not admitted after 3m00s: it held a heavy-leg slot, and the memory 90.0% in use (90 of 100 by the test) never fell below 76%",
            admitted.Refusal);
        Assert.Empty(AdmissionKit.Read(record));
    }

    /// <summary>
    /// A machine whose memory cannot be read takes the leg without it, and its line says so: refused instead, a
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
        Assert.Equal("admitted at once without the memory in use, which could not be read: the test gave no reading", admitted.Fact.Describe());
    }

    /// <summary>
    /// A slot held by a command that has ended - crashed, or killed - is reclaimed by the next leg that looks, and said
    /// to be; one whose command still runs holds its slot whatever name the machine had when it asked. Every entry of
    /// the record is this machine's, since the record is named by what tells the machine apart, so an entry is told by
    /// its process alone: a Mac takes its name from each network it joins.
    /// </summary>
    [Fact]
    public async Task ASlotWhoseCommandHasEnded_IsReclaimed_AndOneStillRunningUnderAnOldNameIsNot()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");

        AdmissionKit.Write(
            record,
            AdmissionKit.Holder(harness, "crashed", processId: int.MaxValue - 1),
            AdmissionKit.Holder(harness, "renamed", machine: "the-name-it-had"));

        using var admitted = await AdmissionKit.Admission(harness, record, new ScriptedGauge(10), new ManualClock())
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 1, maxWaitMinutes: 1), []), TestContext.Current.CancellationToken);

        Assert.False(admitted.Fact.Admitted);
        Assert.Contains("held by '/src/renamed'", admitted.Refusal, StringComparison.Ordinal);
        Assert.Equal(["renamed"], AdmissionKit.Read(record).Select(entry => entry.Leg));
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
        var record = temp.Combine("admission.json");
        var said = new List<string>();
        var waits = 0;

        // While it waits for the memory, its record is removed and another leg takes the machine's one slot.
        var admission = AdmissionKit.Admission(harness, record, new ScriptedGauge(90, 10), new ManualClock(), onWait: () =>
        {
            if (++waits == 1)
            {
                AdmissionKit.Write(record, AdmissionKit.Holder(harness, "newcomer"));
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
        Assert.Equal("admission-0f0e0d0c-0b0a-4908-8706-050403020100.json", HeavyLegSlots.FileNameFor("0f0e0d0c-0b0a-4908-8706-050403020100"));
        Assert.Equal("admission-a-b-c.json", HeavyLegSlots.FileNameFor("a/b\\c"));

        var machine = new HarnessFactory().Platform.MachineId;

        // What the system keeps for it, or - a container its image gave none - its name, and why.
        if (machine.ByName is null)
        {
            Assert.Matches("^[0-9A-Fa-f-]{32,36}$", machine.Id);
        }
        else
        {
            Assert.Equal(Environment.MachineName, machine.Id);
        }

        Assert.Equal(machine, new HarnessFactory().Platform.MachineId);
    }

    /// <summary>
    /// A machine whose system keeps no identifier for it - a container its image gave none - is told by its name, and
    /// that is said with why, since a command started after the name changes keeps a record of its own.
    /// </summary>
    [Fact]
    public void AMachineToldOnlyByItsName_SaysSo_WithWhy()
    {
        var named = new HarnessFactory();
        var platform = Substitute.For<IHostPlatform>();
        platform.MachineId.Returns(new MachineIdentity("build-box", "neither /etc/machine-id nor /var/lib/dbus/machine-id holds one"));

        var path = HeavyLegSlots.PathFor(platform, named.Output);

        Assert.Equal("admission-build-box.json", Path.GetFileName(path));
        Assert.Contains(
            "This machine's heavy legs are recorded under its name, 'build-box', since neither /etc/machine-id nor "
            + "/var/lib/dbus/machine-id holds one; a command started after that name changes keeps a record of its own",
            named.StandardOutput.ToString(),
            StringComparison.Ordinal);

        var identified = new HarnessFactory();
        platform.MachineId.Returns(new MachineIdentity("0f0e0d0c-0b0a-4908-8706-050403020100", null));

        HeavyLegSlots.PathFor(platform, identified.Output);

        Assert.Empty(identified.StandardOutput.ToString());
        Assert.Empty(identified.StandardError.ToString());
    }

    /// <summary>
    /// A slot this process could not give back - a full disk - is never counted by this process's own later legs, which
    /// would otherwise wait behind a leg of their own command that had ended; it leaves the record with the next change.
    /// </summary>
    [Fact]
    public async Task ASlotThatCouldNotBeGivenBack_NeverHoldsThisProcessesOwnLaterLegs()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var disk = new FullDiskFileSystem(harness.FileSystem);
        var clock = new ManualClock();
        var admission = AdmissionKit.Admission(harness, record, new ScriptedGauge(10), clock, fileSystem: disk);
        var rule = AdmissionKit.Rule(heavyLegs: 1);

        var first = await admission.AdmitAsync(AdmissionKit.Request(rule, [], "first"), TestContext.Current.CancellationToken);
        disk.Full = true;
        first.Dispose();
        disk.Full = false;

        Assert.Contains("admission: WARN - leg 'first' could not give its heavy-leg slot back", harness.StandardError.ToString(), StringComparison.Ordinal);
        Assert.Equal("first", Assert.Single(AdmissionKit.Read(record)).Leg);

        using var second = await admission.AdmitAsync(AdmissionKit.Request(rule, [], "second"), TestContext.Current.CancellationToken);

        Assert.Equal("admitted at once, memory 10.0% in use (10 of 100 by the test)", second.Fact.Describe());
        Assert.Equal("second", Assert.Single(AdmissionKit.Read(record)).Leg);
        Assert.Equal(TimeSpan.Zero, clock.Moved);
    }

    /// <summary>
    /// A count that fails once it has been read decides nothing: a leg waiting for the memory to fall is not let start the
    /// moment the count fails, but reads it again next time round, and starts once it reads below the limit.
    /// </summary>
    [Fact]
    public async Task ACountThatFailsOnceRead_DecidesNothing_AndIsReadAgain()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var clock = new ManualClock();
        var said = new List<string>();
        var gauge = new ScriptedGauge(90, null, 50);

        using var admitted = await AdmissionKit.Admission(harness, record, gauge, clock)
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(), said), TestContext.Current.CancellationToken);

        Assert.Null(admitted.Refusal);
        Assert.Equal(50, admitted.Fact.MemoryPercent);
        Assert.Null(admitted.Fact.Unmeasured);
        Assert.Equal(3, gauge.Reads);
        Assert.Equal(TimeSpan.FromSeconds(60), clock.Moved);
        Assert.Contains(
            "holds a heavy-leg slot, and could not read the memory in use again: the test gave no reading; it last read 90.0% in use (90 of 100 by the test)",
            said);
    }

    /// <summary>
    /// One that never reads again is not let start past the machine's wait, naming the count it last read and why it could
    /// not read it again - never let start on a count that stopped while it stood above the limit.
    /// </summary>
    [Fact]
    public async Task ACountThatNeverReadsAgain_IsNotAdmitted_NamingWhatItLastRead()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");

        using var refused = await AdmissionKit.Admission(harness, record, new ScriptedGauge(90, null), new ManualClock())
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(maxWaitMinutes: 1), []), TestContext.Current.CancellationToken);

        Assert.False(refused.Fact.Admitted);
        Assert.Equal(
            "not admitted after 1m00s: it held a heavy-leg slot, and the memory in use, last read as 90.0% in use "
            + "(90 of 100 by the test), could not be read again: the test gave no reading",
            refused.Refusal);
        Assert.Equal(90, refused.Fact.MemoryPercent);
        Assert.Equal("the test gave no reading", refused.Fact.Unmeasured);
        Assert.Empty(AdmissionKit.Read(record));
    }

    /// <summary>A leg stopped while it waits gives its place back, and says no verdict.</summary>
    [Fact]
    public async Task ALegStoppedWhileItWaits_GivesItsPlaceBack()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        using var stop = new CancellationTokenSource();

        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "first"));

        var admission = AdmissionKit.Admission(harness, record, new ScriptedGauge(10), new ManualClock(), onWait: stop.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var admitted = await admission.AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 1), []), stop.Token);
        });

        Assert.Equal(["first"], AdmissionKit.Read(record).Select(entry => entry.Leg));
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
        var admission = AdmissionKit.Admission(slots, new ScriptedGauge(10), new ManualClock());

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
        var record = temp.Combine("admission.json");

        File.WriteAllText(record, "not a record");

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => AdmissionKit.Admission(harness, record, new ScriptedGauge(10), new ManualClock())
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(), []), TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains($"The record of the heavy legs admitted onto this machine '{Path.GetFullPath(record)}' is not readable as JSON", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Remove it once no heavy leg runs or waits on this machine.", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg whose command ended under a name the machine no longer has - a Mac renamed by the network it joined since -
    /// is reclaimed all the same: every entry of the record is this machine's, so it is told by its process alone.
    /// </summary>
    [Fact]
    public async Task ADeadEntryUnderTheMachinesOldName_IsReclaimed()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");

        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "gone", machine: "the-name-it-had", processId: int.MaxValue - 1));

        using var admitted = await AdmissionKit.Admission(harness, record, new ScriptedGauge(10), new ManualClock())
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 1), []), TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(0, admitted.Fact.WaitedSeconds);
        Assert.Contains("Reclaimed a heavy-leg slot from '/src/gone'", harness.StandardOutput.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A holder whose command ends while a leg waits - killed, say, by the very memory this waits on - has its slot
    /// reclaimed by the waiting leg's next look, and said to be once; the leg is taken then, not after its whole wait.
    /// </summary>
    [Fact]
    public async Task AHolderThatEndsWhileALegWaits_IsReclaimedByItsNextLook()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var identity = new EndingProcesses(harness.Identity, 1001);
        var clock = new ManualClock();

        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "killed", processId: 1001, slots: 1));

        var admission = AdmissionKit.Admission(
            new HeavyLegSlots(harness.FileSystem, harness.Output, identity, record),
            new ScriptedGauge(10),
            clock,
            onWait: () => identity.End(1001));

        using var admitted = await admission.AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 1), []), TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(TimeSpan.FromSeconds(AdmissionSettings.DefaultPollSeconds), clock.Moved);
        Assert.Single(
            harness.StandardOutput.ToString().Split('\n'),
            line => line.Contains("Reclaimed a heavy-leg slot from '/src/killed'", StringComparison.Ordinal));
    }

    /// <summary>
    /// Legs are taken in the order they asked: one that asked later never passes one waiting ahead of it, and each
    /// waiting line says how many are ahead and who holds the slots.
    /// </summary>
    [Fact]
    public async Task ALegThatAskedLater_NeverPassesOneWaitingAhead()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var identity = new EndingProcesses(harness.Identity, 1001, 1002);
        var said = new List<string>();
        var waits = 0;

        AdmissionKit.Write(
            record,
            AdmissionKit.Holder(harness, "holding", processId: 1001, slots: 1),
            AdmissionKit.Holder(harness, "waiting", processId: 1002, slots: 1));

        var admission = AdmissionKit.Admission(
            new HeavyLegSlots(harness.FileSystem, harness.Output, identity, record),
            new ScriptedGauge(10),
            new ManualClock(),
            onWait: () => identity.End(++waits == 1 ? 1001 : 1002));

        using var admitted = await admission.AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 1), said), TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(2 * AdmissionSettings.DefaultPollSeconds, admitted.Fact.WaitedSeconds);
        Assert.StartsWith("waits for one of this machine's 1 heavy-leg slot(s), 2 leg(s) ahead; held by '/src/holding'", said[0], StringComparison.Ordinal);
        Assert.StartsWith("waits for one of this machine's 1 heavy-leg slot(s), 1 leg(s) ahead; held by '/src/waiting'", said[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Commands of repositories declaring different counts share one record: a leg starts only while it is fewer legs
    /// from the front than every count up to it allows. So a leg allowing three waits while one allowing one runs, one
    /// allowing one waits while two others run, and a later leg never passes an earlier one whose count holds it back.
    /// </summary>
    [Theory]
    [InlineData(new[] { 1 }, 3, 1, 1)]
    [InlineData(new[] { 3, 3 }, 1, 1, 2)]
    [InlineData(new[] { 1, 1 }, 3, 1, 2)]
    public async Task LegsOfCommandsDeclaringDifferentCounts_StartOnlyWhereEveryCountAheadAllows(int[] ahead, int mine, int inForce, int legsAhead)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var said = new List<string>();

        AdmissionKit.Write(record, [.. ahead.Select((slots, index) => AdmissionKit.Holder(harness, $"other{index}", slots: slots))]);

        using var refused = await AdmissionKit.Admission(harness, record, new ScriptedGauge(10), new ManualClock())
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: mine, maxWaitMinutes: 1), said), TestContext.Current.CancellationToken);

        Assert.False(refused.Fact.Admitted);
        Assert.StartsWith($"waits for one of this machine's {inForce} heavy-leg slot(s), {legsAhead} leg(s) ahead", said[0], StringComparison.Ordinal);
        Assert.Equal(ahead.Length, AdmissionKit.Read(record).Count);
    }

    /// <summary>A leg taking a slot beside others whose counts allow it starts at once, its own count keeping theirs.</summary>
    [Fact]
    public async Task ALegWhoseCountAndEveryCountAheadAllowIt_StartsAtOnce()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");

        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "first", slots: 3), AdmissionKit.Holder(harness, "second", slots: 3));

        using var admitted = await AdmissionKit.Admission(harness, record, new ScriptedGauge(10), new ManualClock(), settle: TimeSpan.Zero)
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 3), []), TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal([3, 3, 3], AdmissionKit.Read(record).Select(entry => entry.Slots));
    }

    /// <summary>
    /// However many legs ask at once, no more hold a slot at a time than the machine allows, every one is taken in the
    /// end, and every slot is given back: the ask, the look and the give-back are each one step under the machine's lock.
    /// </summary>
    [Fact]
    public async Task ManyLegsAskingAtOnce_NeverRunMoreThanTheSlots()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var admission = new LegAdmission(
            AdmissionKit.Slots(harness, record),
            new ScriptedGauge(10),
            TimeProvider.System,
            (_, token) => Task.Delay(TimeSpan.FromMilliseconds(5), token),
            (least, _) => least);
        var rule = AdmissionKit.Rule(heavyLegs: 2, settleLeast: 0, settleMost: 0);
        var running = 0;
        var most = 0;

        async Task RunAsync(int leg)
        {
            using var admitted = await admission.AdmitAsync(AdmissionKit.Request(rule, [], $"leg{leg}"), TestContext.Current.CancellationToken);

            Assert.True(admitted.Fact.Admitted);

            var now = Interlocked.Increment(ref running);

            lock (admission)
            {
                most = Math.Max(most, now);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
            Interlocked.Decrement(ref running);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(leg => Task.Run(() => RunAsync(leg), TestContext.Current.CancellationToken)));

        Assert.InRange(most, 1, 2);
        Assert.Empty(AdmissionKit.Read(record));
    }

    /// <summary>The last wait is cut to what is left of the machine's: a poll longer than that never outlasts it.</summary>
    [Fact]
    public async Task ALegsLastWait_IsCutToWhatIsLeftOfItsWait()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var clock = new ManualClock();

        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "first"), AdmissionKit.Holder(harness, "second"));

        using var refused = await AdmissionKit.Admission(harness, record, new ScriptedGauge(10), clock)
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(pollSeconds: 45, maxWaitMinutes: 1), []), TestContext.Current.CancellationToken);

        Assert.False(refused.Fact.Admitted);
        Assert.Equal(60, refused.Fact.WaitedSeconds);
        Assert.Equal(TimeSpan.FromMinutes(1), clock.Moved);
    }

    /// <summary>
    /// A leg is let start only at a reading its own line shows below the limit: one of 75.96 against 76 reads as 76.0,
    /// so it waits, where starting would put 76.0% on a line saying the limit was 76%.
    /// </summary>
    [Fact]
    public async Task AReadingTheLineShowsAtTheLimit_IsNotBelowIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var said = new List<string>();

        using var admitted = await AdmissionKit.Admission(harness, temp.Combine("admission.json"), new ScriptedGauge(75.96, 75.94), new ManualClock())
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(), said), TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(75.9, admitted.Fact.MemoryPercent);
        Assert.Equal(AdmissionSettings.DefaultPollSeconds, admitted.Fact.WaitedSeconds);
        Assert.Contains("holds a heavy-leg slot, and waits for the memory 76.0% in use (75.96 of 100 by the test) to fall below 76%", said);
    }

    /// <summary>
    /// A leg whose readings fell below the limit only to rise above it again before it could start is not said to have waited
    /// on a memory that never fell: its refusal says what happened.
    /// </summary>
    [Fact]
    public async Task ALegWhoseMemoryFellOnlyToRiseAgain_IsNotTaken_SayingSo()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");

        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "neighbour"));

        using var refused = await AdmissionKit.Admission(harness, record, new ScriptedGauge(70, 80), new ManualClock(), settle: TimeSpan.FromSeconds(20))
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(maxWaitMinutes: 1), []), TestContext.Current.CancellationToken);

        Assert.False(refused.Fact.Admitted);
        Assert.Equal(
            "not admitted after 1m00s: it held a heavy-leg slot, and the memory fell below 76% only to rise above it again "
            + "before the leg could start; it last read 80.0% in use (80 of 100 by the test)",
            refused.Refusal);
    }

    /// <summary>A place given back is never looked at, nor held, again: a leg that gave its place back asks again.</summary>
    [Fact]
    public void APlaceGivenBack_IsNeverHeldAgain()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var slots = AdmissionKit.Slots(harness, record);
        var place = slots.Ask("run-mine", "build", "mine", "local", "/src/mine", null, 1);

        place.Dispose();

        Assert.Throws<ObjectDisposedException>(() => slots.Look(place));
        Assert.Empty(AdmissionKit.Read(record));
    }

    /// <summary>
    /// An entry of the record that leaves out what it needs - written by hand, or by something else - is refused as the
    /// record's not being this build's, never read in as a count of none that would hold every leg back.
    /// </summary>
    [Fact]
    public async Task AnEntryLeavingOutWhatItNeeds_IsRefused_NeverReadAsNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");

        File.WriteAllText(
            record,
            $$"""[{ "machine": "box", "processId": {{harness.Identity.CurrentId}}, "runId": "r", "askedUtc": "2026-09-30T16:29:42+00:00", "command": "build", "leg": "old", "host": "local", "tree": "/src/old" }]""");

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => AdmissionKit.Admission(harness, record, new ScriptedGauge(10), new ManualClock())
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(), []), TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("is not readable as JSON", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("slots", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg whose build needs room claims it as it is let start, its line saying what it took of what was free, and gives it
    /// back with its slot as its work ends. Counted only within one command, two commands each placing one leg on one host
    /// both found it room, and filled its disk at build step 931 of 1295.
    /// </summary>
    [Fact]
    public async Task ALegWhoseBuildNeedsRoom_ClaimsIt_AndGivesItBackWithItsSlot()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var room = new ScriptedRoom(harness.FileSystem, 40 * AdmissionKit.Gibibyte);
        var said = new List<string>();

        using (var admitted = await AdmissionKit.Admission(harness, record, new ScriptedGauge(30), new ManualClock(), fileSystem: room)
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(), said, room: AdmissionKit.Room(10)), TestContext.Current.CancellationToken))
        {
            Assert.True(admitted.Fact.Admitted);
            Assert.Equal("admitted at once, memory 30.0% in use (30 of 100 by the test); room ~10 GiB of 40 GiB free on '/data'", admitted.Fact.Describe());

            var claim = Assert.Single(AdmissionKit.ReadClaims(record));
            Assert.Equal(("mine", "/data", 10 * AdmissionKit.Gibibyte), (claim.Holder.Leg, claim.Filesystem, claim.Bytes));
        }

        Assert.Empty(AdmissionKit.ReadClaims(record));
        Assert.Empty(AdmissionKit.Read(record));
    }

    /// <summary>
    /// A leg whose build would not fit beside what the other admitted legs on its filesystem claim waits for room, saying
    /// what is free, who claims what, and what it needs; it starts once a claim is given back. Their claims count whole,
    /// though some of it may be written already: it waits a little longer, rather than starting into a disk it fills.
    /// </summary>
    [Fact]
    public async Task ALegWaitsForTheRoomOtherLegsClaim_AndStartsOnceItIsGivenBack()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var room = new ScriptedRoom(harness.FileSystem, 40 * AdmissionKit.Gibibyte);
        var said = new List<string>();
        var waits = 0;

        AdmissionKit.WriteClaims(record, new RoomClaim(AdmissionKit.Holder(harness, "first"), 35 * AdmissionKit.Gibibyte, "/data"));

        var admission = AdmissionKit.Admission(harness, record, new ScriptedGauge(30), new ManualClock(), fileSystem: room, onWait: () =>
        {
            if (++waits == 2)
            {
                AdmissionKit.WriteClaims(record);
            }
        });

        using var admitted = await admission.AdmitAsync(
            AdmissionKit.Request(AdmissionKit.Rule(settleLeast: 0, settleMost: 0, pollSeconds: 30), said, room: AdmissionKit.Room(10)),
            TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(60, admitted.Fact.WaitedSeconds);

        var waiting = Assert.Single(said, line => line.StartsWith("holds a heavy-leg slot, and waits for room", StringComparison.Ordinal));
        Assert.StartsWith("holds a heavy-leg slot, and waits for room: 40 GiB free on '/data', beside ~35 GiB claimed by '/src/first'", waiting, StringComparison.Ordinal);
        Assert.EndsWith("and this leg needs ~10 GiB, as the test says", waiting, StringComparison.Ordinal);
        Assert.Equal("mine", Assert.Single(AdmissionKit.ReadClaims(record)).Holder.Leg);
    }

    /// <summary>
    /// A leg that never finds room within its machine's wait is not admitted, its line naming the room, who claimed it and
    /// where the claims are recorded, and claims nothing.
    /// </summary>
    [Fact]
    public async Task ALegThatNeverFindsRoom_IsNotAdmitted_NamingWhoClaimedIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var room = new ScriptedRoom(harness.FileSystem, 40 * AdmissionKit.Gibibyte);
        var said = new List<string>();

        AdmissionKit.WriteClaims(record, new RoomClaim(AdmissionKit.Holder(harness, "first"), 35 * AdmissionKit.Gibibyte, "/data"));

        using var admitted = await AdmissionKit.Admission(harness, record, new ScriptedGauge(30), new ManualClock(), fileSystem: room)
            .AdmitAsync(
                AdmissionKit.Request(AdmissionKit.Rule(settleLeast: 0, settleMost: 0, pollSeconds: 30, maxWaitMinutes: 2), said, room: AdmissionKit.Room(10)),
                TestContext.Current.CancellationToken);

        Assert.False(admitted.Fact.Admitted);
        Assert.StartsWith("not admitted after 2m00s: it held a heavy-leg slot, and its build would not fit: 40 GiB free on '/data', beside ~35 GiB claimed by '/src/first'", admitted.Refusal, StringComparison.Ordinal);
        Assert.EndsWith($"; the room each heavy leg claims is recorded in '{HeavyLegSlots.RoomPathFor(record)}'", admitted.Refusal, StringComparison.Ordinal);
        Assert.Single(admitted.Fact.Holders ?? []);

        // Its line points at the record of the room, where the legs it names are, and says the room it last read.
        Assert.Equal(HeavyLegSlots.RoomPathFor(record), admitted.Fact.Record);
        Assert.StartsWith("40 GiB free on '/data', beside ~35 GiB claimed by '/src/first'", admitted.Fact.Room, StringComparison.Ordinal);
        Assert.Equal("first", Assert.Single(AdmissionKit.ReadClaims(record)).Holder.Leg);
        Assert.Empty(AdmissionKit.Read(record));
    }

    /// <summary>
    /// What counts against a leg's room is what other live legs claim on its own filesystem: a claim on another filesystem
    /// counts nothing, and one whose command has ended is reclaimed, said to be.
    /// </summary>
    [Fact]
    public async Task OnlyLiveClaimsOnItsOwnFilesystem_CountAgainstALegsRoom()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var room = new ScriptedRoom(harness.FileSystem, 40 * AdmissionKit.Gibibyte);
        var said = new List<string>();

        AdmissionKit.WriteClaims(
            record,
            new RoomClaim(AdmissionKit.Holder(harness, "elsewhere"), 35 * AdmissionKit.Gibibyte, "/other"),
            new RoomClaim(AdmissionKit.Holder(harness, "gone", processId: 999_999), 35 * AdmissionKit.Gibibyte, "/data"));

        using var admitted = await AdmissionKit.Admission(harness, record, new ScriptedGauge(30), new ManualClock(), fileSystem: room)
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(), said, room: AdmissionKit.Room(10)), TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(0, admitted.Fact.WaitedSeconds);
        Assert.Equal(["elsewhere", "mine"], AdmissionKit.ReadClaims(record).Select(claim => claim.Holder.Leg));
        Assert.Contains("a claim on this machine's room", harness.StandardError.ToString() + harness.StandardOutput.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg whose room cannot be read in its wait is let start, its line saying why, as a leg placed where its room was
    /// unmeasured is - its need claimed against every filesystem of the machine, since nothing says which one it fills, so
    /// a leg on any of them counts it - and a leg whose build needs nothing anyone said is never asked.
    /// </summary>
    [Fact]
    public async Task ARoomThatCannotBeRead_LetsTheLegStart_ClaimedAgainstEveryFilesystem()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var unread = new ScriptedRoom(harness.FileSystem, 40 * AdmissionKit.Gibibyte) { Unreadable = "the volume is gone" };
        var elsewhere = new ScriptedRoom(harness.FileSystem, 40 * AdmissionKit.Gibibyte, filesystem: "/other");
        var said = new List<string>();

        using (var admitted = await AdmissionKit.Admission(harness, record, new ScriptedGauge(30), new ManualClock(), fileSystem: unread)
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(), said, room: AdmissionKit.Room(35)), TestContext.Current.CancellationToken))
        {
            Assert.True(admitted.Fact.Admitted);
            Assert.Equal("unread, so ~35 GiB is claimed against every filesystem here: the volume is gone", admitted.Fact.Room);

            var claim = Assert.Single(AdmissionKit.ReadClaims(record));
            Assert.Null(claim.Filesystem);

            // A leg on another filesystem counts it, and does not fit beside it.
            using var beside = await AdmissionKit.Admission(harness, record, new ScriptedGauge(30), new ManualClock(), fileSystem: elsewhere)
                .AdmitAsync(
                    AdmissionKit.Request(AdmissionKit.Rule(settleLeast: 0, settleMost: 0, pollSeconds: 30, maxWaitMinutes: 1), said, "other", AdmissionKit.Room(10, "other")),
                    TestContext.Current.CancellationToken);

            Assert.False(beside.Fact.Admitted);
            Assert.Contains("beside ~35 GiB claimed by '/src/mine'", beside.Refusal, StringComparison.Ordinal);
        }

        Assert.Empty(AdmissionKit.ReadClaims(record));

        using (var admitted = await AdmissionKit.Admission(harness, record, new ScriptedGauge(30), new ManualClock(), fileSystem: unread)
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(), said), TestContext.Current.CancellationToken))
        {
            Assert.True(admitted.Fact.Admitted);
            Assert.Null(admitted.Fact.Room);
        }

        Assert.Empty(AdmissionKit.ReadClaims(record));
    }

    /// <summary>
    /// A room read in a leg's wait and not again decides nothing, as a memory reading lost does not: the leg keeps waiting,
    /// saying what it last read, and is not admitted at the end of its wait, naming that reading and why it could not read
    /// it again. Let start on the lost reading, it would have started into the very disk it waited for.
    /// </summary>
    [Fact]
    public async Task ARoomThatStopsReading_DecidesNothing_AndIsNotAdmittedNamingWhatItLastRead()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var room = new ScriptedRoom(harness.FileSystem, 40 * AdmissionKit.Gibibyte) { Unreadable = "the volume went away", ReadsBeforeUnreadable = 1 };
        var said = new List<string>();

        AdmissionKit.WriteClaims(record, new RoomClaim(AdmissionKit.Holder(harness, "first"), 35 * AdmissionKit.Gibibyte, "/data"));

        using var admitted = await AdmissionKit.Admission(harness, record, new ScriptedGauge(30), new ManualClock(), fileSystem: room)
            .AdmitAsync(
                AdmissionKit.Request(AdmissionKit.Rule(settleLeast: 0, settleMost: 0, pollSeconds: 30, maxWaitMinutes: 2), said, room: AdmissionKit.Room(10)),
                TestContext.Current.CancellationToken);

        Assert.False(admitted.Fact.Admitted);
        Assert.StartsWith("not admitted after 2m00s: it held a heavy-leg slot, and the room, last read as 40 GiB free on '/data'", admitted.Refusal, StringComparison.Ordinal);
        Assert.EndsWith("could not be read again: the volume went away", admitted.Refusal, StringComparison.Ordinal);
        Assert.Single(said, line => line.StartsWith("holds a heavy-leg slot, and could not read the room again: the volume went away", StringComparison.Ordinal));
        Assert.Equal("first", Assert.Single(AdmissionKit.ReadClaims(record)).Holder.Leg);
    }

    /// <summary>
    /// A leg that waited for room settles the memory again before it starts, as one taking a slot does: another leg may
    /// have started while it waited, and a reading taken before that showed nothing of it.
    /// </summary>
    [Fact]
    public async Task ALegThatWaitedForRoom_SettlesTheMemoryAgainBeforeItStarts()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var room = new ScriptedRoom(harness.FileSystem, 40 * AdmissionKit.Gibibyte);
        var said = new List<string>();
        var clock = new ManualClock();
        var waits = 0;

        // Another leg holds a slot, so every reading below the limit is settled; and claims the room until the first poll.
        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "first"));
        AdmissionKit.WriteClaims(record, new RoomClaim(AdmissionKit.Holder(harness, "first"), 35 * AdmissionKit.Gibibyte, "/data"));

        var admission = AdmissionKit.Admission(harness, record, new ScriptedGauge(30), clock, settle: TimeSpan.FromSeconds(5), fileSystem: room, onWait: () =>
        {
            if (++waits == 2)
            {
                AdmissionKit.WriteClaims(record);
            }
        });

        using var admitted = await admission.AdmitAsync(
            AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 2, settleLeast: 5, settleMost: 5, pollSeconds: 30), said, room: AdmissionKit.Room(10)),
            TestContext.Current.CancellationToken);

        // A settle, a poll for room, and a settle again: 40 seconds, where starting on the first settle would be 35.
        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(40, admitted.Fact.WaitedSeconds);
    }

    /// <summary>
    /// Two legs asking at once for the one room left never both take it: the claim is read and written under the room
    /// record's lock, so one is let start and the other waits, and is not admitted where the first holds it to the end.
    /// </summary>
    [Fact]
    public async Task TwoLegsAskingAtOnceForTheOneRoomLeft_NeverBothTakeIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var room = new ScriptedRoom(harness.FileSystem, 15 * AdmissionKit.Gibibyte);
        var rule = AdmissionKit.Rule(heavyLegs: 2, settleLeast: 0, settleMost: 0, pollSeconds: 30, maxWaitMinutes: 1);

        var admissions = await Task.WhenAll(
            Task.Run(() => AdmissionKit.Admission(harness, record, new ScriptedGauge(30), new ManualClock(), fileSystem: room)
                .AdmitAsync(AdmissionKit.Request(rule, [], "a", AdmissionKit.Room(10, "a")), TestContext.Current.CancellationToken)),
            Task.Run(() => AdmissionKit.Admission(harness, record, new ScriptedGauge(30), new ManualClock(), fileSystem: room)
                .AdmitAsync(AdmissionKit.Request(rule, [], "b", AdmissionKit.Room(10, "b")), TestContext.Current.CancellationToken)));

        try
        {
            Assert.Single(admissions, admitted => admitted.Fact.Admitted);
            Assert.Contains("its build would not fit", Assert.Single(admissions, admitted => !admitted.Fact.Admitted).Refusal, StringComparison.Ordinal);
            Assert.Single(AdmissionKit.ReadClaims(record));
        }
        finally
        {
            foreach (var admitted in admissions)
            {
                admitted.Dispose();
            }
        }
    }

    /// <summary>This process as a test's holders see it: each id it names alive until the test ends it.</summary>
    /// <param name="real">This process.</param>
    /// <param name="alive">The ids of the other commands' processes, alive until ended.</param>
    private sealed class EndingProcesses(IProcessIdentity real, params int[] alive) : IProcessIdentity
    {
        private readonly HashSet<int> _alive = [.. alive];

        public int CurrentId => real.CurrentId;

        public string CurrentMachine => real.CurrentMachine;

        public string? Current => real.Current;

        public bool IsAlive(int processId, string? stamp)
            => processId == real.CurrentId ? real.IsAlive(processId, stamp) : _alive.Contains(processId);

        public void End(int processId) => _alive.Remove(processId);
    }
}
