using NSubstitute;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;
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

        // The first holder's command ends while this leg waits its second poll.
        var waits = 0;
        var admission = AdmissionKit.Admission(harness, record, new ScriptedGauge(30), clock, onWait: () =>
        {
            if (++waits == 2)
            {
                AdmissionKit.Write(record, [.. AdmissionKit.Read(record).Where(entry => entry.Leg != "first")]);
            }
        });

        // No settle: what the settle is for is the next test's.
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
        var record = temp.Combine("admission.json");

        File.WriteAllText(record, "not a record");

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => AdmissionKit.Admission(harness, record, new ScriptedGauge(10), new ManualClock())
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(), []), TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains($"The record of the heavy legs admitted onto this machine '{Path.GetFullPath(record)}' is not readable as JSON", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Remove it once no heavy leg runs or waits on this machine.", refusal.Message, StringComparison.Ordinal);
    }
}
