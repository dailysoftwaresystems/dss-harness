using System.Globalization;
using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>A machine's memory in use, as a test says it stands: one reading per look, the last one kept.</summary>
/// <param name="percents">Each reading in turn; <see langword="null"/> for one that cannot be read.</param>
internal sealed class ScriptedGauge(params double?[] percents) : IMemoryGauge
{
    private readonly Queue<double?> _readings = new(percents);
    private double? _last;

    /// <summary>How many times the memory was read.</summary>
    public int Reads { get; private set; }

    public (MemoryReading? Reading, string? Unmeasured) Read()
    {
        Reads++;

        if (_readings.Count > 0)
        {
            _last = _readings.Dequeue();
        }

        return _last is { } percent
            ? (new MemoryReading(percent, string.Create(CultureInfo.InvariantCulture, $"{percent} of 100 by the test")), null)
            : (null, "the test gave no reading");
    }
}

/// <summary>Builds what a test of heavy-leg admission needs: slots kept in a file of its own, a clock it moves, and waits that pass at once.</summary>
internal static class AdmissionKit
{
    /// <summary>
    /// An admission over the slots kept at <paramref name="record"/>, on <paramref name="fileSystem"/> or the real one;
    /// see <see cref="Admission(HeavyLegSlots, IMemoryGauge, ManualClock, TimeSpan?, Action?)"/>.
    /// </summary>
    public static LegAdmission Admission(
        HarnessFactory harness,
        string record,
        IMemoryGauge gauge,
        ManualClock clock,
        TimeSpan? settle = null,
        Action? onWait = null,
        IFileSystem? fileSystem = null)
        => Admission(Slots(harness, record, fileSystem), gauge, clock, settle, onWait);

    /// <summary>
    /// An admission over <paramref name="slots"/>, whose every wait moves <paramref name="clock"/> by what it waits and
    /// then does <paramref name="onWait"/>, and whose every settle lasts <paramref name="settle"/>, or its least where
    /// none is given.
    /// </summary>
    public static LegAdmission Admission(HeavyLegSlots slots, IMemoryGauge gauge, ManualClock clock, TimeSpan? settle = null, Action? onWait = null)
        => new(
            slots,
            gauge,
            clock,
            (delay, token) =>
            {
                token.ThrowIfCancellationRequested();
                clock.Advance(delay);
                onWait?.Invoke();
                return Task.CompletedTask;
            },
            (least, _) => settle ?? least);

    /// <summary>The slots kept at <paramref name="record"/>, on <paramref name="fileSystem"/> or the real one.</summary>
    public static HeavyLegSlots Slots(HarnessFactory harness, string record, IFileSystem? fileSystem = null)
        => new(fileSystem ?? harness.FileSystem, harness.Output, harness.Identity, record);

    /// <summary>A rule with the numbers a test gives, the rest the built-in values a section left out takes.</summary>
    public static AdmissionRule Rule(
        int heavyLegs = AdmissionSettings.DefaultHeavyLegs,
        double maxMemoryPercent = AdmissionSettings.DefaultMaxMemoryPercent,
        int settleLeast = AdmissionSettings.DefaultSettleLeastSeconds,
        int settleMost = AdmissionSettings.DefaultSettleMostSeconds,
        int pollSeconds = AdmissionSettings.DefaultPollSeconds,
        double maxWaitMinutes = AdmissionSettings.DefaultMaxWaitMinutes)
        => new(
            heavyLegs,
            maxMemoryPercent,
            TimeSpan.FromSeconds(settleLeast),
            TimeSpan.FromSeconds(settleMost),
            TimeSpan.FromSeconds(pollSeconds),
            TimeSpan.FromMinutes(maxWaitMinutes));

    /// <summary>A leg asking by <paramref name="rule"/>, whose every line of progress goes to <paramref name="said"/>.</summary>
    public static AdmissionRequest Request(AdmissionRule rule, List<string> said, string leg = "mine")
        => new(rule, $"run-{leg}", "build", leg, "local", $"/src/{leg}", "x86_64-gcc-debug", said.Add);

    /// <summary>
    /// A leg of another command holding a slot, or waiting for one, whose command allows <paramref name="slots"/> at once:
    /// this process stands for that command, so the entry stands for as long as the test runs, whatever machine name it
    /// carries, unless it names a process that has gone.
    /// </summary>
    public static SlotEntry Holder(HarnessFactory harness, string leg, string? machine = null, int? processId = null, int slots = AdmissionSettings.DefaultHeavyLegs)
        => new(
            machine ?? harness.Identity.CurrentMachine,
            processId ?? harness.Identity.CurrentId,
            $"run-{leg}",
            new DateTimeOffset(2026, 9, 30, 16, 29, 42, TimeSpan.Zero),
            "test",
            leg,
            "local",
            $"/src/{leg}",
            slots,
            processId is null ? harness.Identity.Current : "a-process-that-has-gone",
            "x86_64-gcc-release");

    /// <summary>Writes <paramref name="entries"/> as the slots' record, as another command would have left it.</summary>
    public static void Write(string record, params SlotEntry[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        File.WriteAllText(record, JsonSerializer.Serialize(entries, JsonStateFile.Options));
    }

    /// <summary>What the slots' record holds now.</summary>
    public static IReadOnlyList<SlotEntry> Read(string record)
        => File.Exists(record) ? JsonSerializer.Deserialize<List<SlotEntry>>(File.ReadAllText(record), JsonStateFile.Options) ?? [] : [];
}
