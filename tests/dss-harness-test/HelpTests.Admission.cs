using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Anchors;
using DssHarness.Core.Ci;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Help;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

public sealed partial class HelpTests
{
    /// <summary>
    /// The admission topic, under either name, names every key a section may declare with the value it takes when left
    /// out and its bounds, and the exit a leg not admitted ends with - each read from the code, so none can drift.
    /// </summary>
    [Theory]
    [InlineData("admission")]
    [InlineData("heavy")]
    public async Task TheAdmissionTopic_NamesEveryKeyWithItsDefault_FromTheCode(string topic)
    {
        var result = HelpText.Of(topic);


        var keys = typeof(AdmissionSettings)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .ToList();

        Assert.Equal(["heavyLegs", "maxMemoryPercent", "settleSeconds", "pollSeconds", "maxWaitMinutes", "minFreeGiB", "reclaimIdleBuilds", "processes", "agents", "ageAfterMinutes"], keys);

        foreach (var key in keys)
        {
            Assert.Contains($"  {key}", result.StandardOutput, StringComparison.Ordinal);
        }

        foreach (var value in new[]
        {
            $"({AdmissionSettings.DefaultHeavyLegs})",
            string.Create(CultureInfo.InvariantCulture, $"({AdmissionSettings.DefaultMaxMemoryPercent})"),
            $"([{string.Join(", ", AdmissionSettings.DefaultSettleSeconds)}])",
            $"({AdmissionSettings.DefaultPollSeconds})",
            string.Create(CultureInfo.InvariantCulture, $"({AdmissionSettings.DefaultMaxWaitMinutes})"),
            string.Create(CultureInfo.InvariantCulture, $"from 0, however large; 0 stops no build ({AdmissionSettings.DefaultMinFreeGiB})"),
            $"at most {AdmissionSettings.MostSeconds}",
            string.Create(CultureInfo.InvariantCulture, $"at most {AdmissionSettings.MostWaitMinutes} minutes"),
            $"exit {LegExit.NotAdmitted}",
        })
        {
            Assert.Contains(value, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The admission topic says the dispatch queue's whole rule, its numbers read from the code: slots granted in its fair
    /// order and held to their end, the round-robin's share, the cost from the newest runs a name kept and one quantum
    /// where none is, the first unit that does not fit holding back those behind it, the aging bound's range and its 0,
    /// and the records the queue keeps beside the slots.
    /// </summary>
    [Fact]
    public void TheAdmissionTopic_SaysTheDispatchQueuesRule_FromTheCode()
    {
        var said = Prose.Words(HelpText.Of("admission").StandardOutput);

        foreach (var expected in new[]
        {
            "given in the dispatch queue's fair order, below;",
            "A unit holds a slot once the queue grants it one - written in the same change of the record that grants it - and holds it to its end: nothing is pre-empted.",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Each round, a tenant with a unit waiting is credited its share: its weight ({DispatchOrder.LocalWeight}) times its run's priority (a workflow's priority key, {DispatchOrder.LeastPriority} where none), doubled for each whole period of ageAfterMinutes its unit has waited"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"A unit's cost is the median of the newest {UnitDurations.Kept} times a unit of its name - its repository, its command and its leg, or a sweep's worker or arm - held its slot on this machine, in minutes, and {UnitDurations.Quantum} where none is kept"),
            "The unit the order puts first that does not fit - its own count allows fewer legs at once than hold slots - holds back every unit behind it until it fits, so none is passed for ever.",
            string.Create(
                CultureInfo.InvariantCulture,
                $"from 0, where every unit is served in the order it asked, to the most minutes a span of time holds, {AdmissionSettings.MostAgeAfterMinutes} ({AdmissionSettings.DefaultAgeAfterMinutes})"),
            "admission-<machine id>.queue.json, the times units of each name held their slots in admission-<machine id>.durations",
            $"once, at least 1 ({AdmissionSettings.MachinesProcessors}); shown by queue, asked by no step yet",
            $"once, at least 1 ({AdmissionSettings.DefaultAgents}); shown by queue, asked by no agent yet",
        })
        {
            Assert.Contains(expected, said, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("in the order legs asked", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The admission topic says a waiting leg says where it stands at least every five minutes, the minutes read from
    /// the code, and that a poll or a settle longer than that keeps to it.
    /// </summary>
    [Fact]
    public async Task TheAdmissionTopic_SaysAWaitNeverGoesSilent_WhateverThePollAndTheSettle()
    {
        var said = Prose.Words((HelpText.Of("admission")).StandardOutput);

        Assert.Contains(
            $"It says so again, as it reads then and with how long it has waited, at least every {LegAdmission.SaidAgainEvery.TotalMinutes:0} minutes - "
            + "a poll longer than that is cut at it, and a settle longer than that waited whole in pieces of it - so a long wait never goes silent.",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The admission topic says a heavy leg's build is held to minFreeGiB - read every few seconds, the seconds read from
    /// the code, and again as a build or a write of its fails - and stopped under it, writing nothing where it starts under
    /// it; that WSL's page cache is dropped before a wait on the
    /// memory and as a WSL leg ends, a drop that could not be made said with why; that a holder's line never names its
    /// machine; and that a wait with only legs of its own run ahead of it does not count.
    /// </summary>
    [Fact]
    public async Task TheAdmissionTopic_SaysTheFloor_TheCacheDrop_TheHolderUnnamed_AndTheOwnRunsWait()
    {
        var said = Prose.Words((HelpText.Of("admission")).StandardOutput);

        foreach (var expected in new[]
        {
            $"While a heavy leg builds, each filesystem its build fills is read again every {RoomFloorWatch.Every.TotalSeconds:0} seconds, and the "
                + $"build is stopped once one has less free than minFreeGiB: stopped, exit {HarnessExit.Incomplete}, naming what was free and where, "
                + "and what it built left for clean - and so is a build that failed with one under it, read again as it failed, since a full disk "
                + "says nothing of its code, and one whose own write failed then - a phase's log, its record. A build under it as it starts writes "
                + "nothing, its record included, and a record it has no room left for is said, the one it wrote as it began standing.",
            "So a leg about to wait on the memory has that cache dropped first, as root (sync, then drop_caches) in a running distribution a WSL "
                + "host of its repository reaches - at most once a minute, whichever leg asks - and reads the memory again once what was dropped "
                + "has come back, a minute later at the most, its line saying how much was dropped and the memory before and after, and goes on "
                + "from that reading as from any other.",
            "A WSL leg sent from a machine that admits drops it as it ends, without waiting, its line saying so - or that WSL listed none of its "
                + "distributions running, though the leg had just run in one.",
            "a drop that could not be made is said once - a WSL host whose item cannot be read, where no other reaches one, or a list of the "
                + "distributions running that cannot be read, among the reasons; that list is read in UTF-16 too, which an older wsl.exe writes "
                + "whatever WSL_UTF8 says.",
            "the distribution reaches through its mount there: /mnt/c, as /proc/mounts lists it by the drive, or by the path its options name "
                + "where an older WSL lists what it mounts as drvfs.",
            "process, run, and since when it asked, never the machine's name, which only the record keeps",
            "A wait with only legs of its own run ahead of it - holding slots, or in line first: a command's WSL leg behind its Windows legs, "
                + "asked for at once - is certain to end and does not count: its line says they are its own, and a refusal after it says how much "
                + "of the wait did not count. Another command's leg in line ahead makes it count, whoever holds the slots. On an ssh host, the "
                + "legs one command sends there ask under that command's run, and at its run's priority.",
        })
        {
            Assert.Contains(expected, said, StringComparison.Ordinal);
        }
    }
}
