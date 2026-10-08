using System.Globalization;
using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>A machine's memory in use, as a test says it stands: one reading per look, the last one kept.</summary>
/// <param name="percents">Each reading in turn; <see langword="null"/> for one that cannot be read.</param>
internal sealed class ScriptedGauge(params double?[] percents) : IMemoryGauge
{
    private readonly Queue<double?> _readings = new(percents);
    private readonly Lock _reading = new();
    private double? _last;

    /// <summary>How many times the memory was read.</summary>
    public int Reads { get; private set; }

    /// <summary>The next reading; read by legs at once, one at a time.</summary>
    public (MemoryReading? Reading, string? Unmeasured) Read()
    {
        lock (_reading)
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
}

/// <summary>
/// The real file system, save that every path's room is on one filesystem, holding what the test says it does now, and
/// every path whose room is asked about is remembered.
/// </summary>
/// <param name="inner">The real file system.</param>
/// <param name="freeBytes">What is free there at first.</param>
/// <param name="filesystem">The filesystem every path is on.</param>
internal sealed class ScriptedRoom(Core.FileSystem.IFileSystem inner, long freeBytes, string filesystem = "/data") : PassThroughFileSystem(inner)
{
    private readonly Lock _asking = new();

    /// <summary>What is free there now.</summary>
    public long FreeBytes { get; set; } = freeBytes;

    /// <summary>Why the room cannot be read, where the test says it cannot.</summary>
    public string? Unreadable { get; set; }

    /// <summary>How many readings succeed before every later one fails as <see cref="Unreadable"/> says; all, where null.</summary>
    public int? ReadsBeforeUnreadable { get; set; }

    /// <summary>Every path whose room was asked about, in order.</summary>
    public List<string> Asked { get; } = [];

    public override DiskSpace SpaceAt(string path)
    {
        lock (_asking)
        {
            Asked.Add(path);

            var unreadable = Unreadable is not null && (ReadsBeforeUnreadable is not { } reads || Asked.Count > reads);

            return unreadable
                ? throw new IOException(Unreadable)
                : new DiskSpace(FreeBytes, 100 * AdmissionKit.Gibibyte, filesystem);
        }
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

    /// <summary>
    /// A leg asking by <paramref name="rule"/>, whose every line of progress goes to <paramref name="said"/>, and whose
    /// build needs <paramref name="room"/> where it says.
    /// </summary>
    public static AdmissionRequest Request(AdmissionRule rule, List<string> said, string leg = "mine", RoomNeed? room = null)
        => new(rule, $"run-{leg}", "build", leg, "local", $"/src/{leg}", "x86_64-gcc-debug", said.Add, room);

    /// <summary>What a test's leg's build needs: <paramref name="gibibytes"/> on the filesystem of its build directory.</summary>
    public static RoomNeed Room(double gibibytes, string leg = "mine")
        => new((long)(gibibytes * Gibibyte), "as the test says", $"/src/{leg}/build", string.Empty);

    /// <summary>Writes <paramref name="claims"/> as the room's record beside the slots' at <paramref name="record"/>.</summary>
    public static void WriteClaims(string record, params RoomClaim[] claims)
    {
        var path = HeavyLegSlots.RoomPathFor(record);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(claims, JsonStateFile.Options));
    }

    /// <summary>What the room's record beside the slots' at <paramref name="record"/> holds now.</summary>
    public static IReadOnlyList<RoomClaim> ReadClaims(string record)
        => File.Exists(HeavyLegSlots.RoomPathFor(record))
            ? JsonSerializer.Deserialize<List<RoomClaim>>(File.ReadAllText(HeavyLegSlots.RoomPathFor(record)), JsonStateFile.Options) ?? []
            : [];

    /// <summary>A gibibyte, as a test counts room.</summary>
    public const long Gibibyte = 1L << 30;

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

    /// <summary>
    /// What the slots' record holds now, and the room's beside it, read while legs are asking: each under its record's
    /// own machine-wide step, as every reader of one is. Read past it, a read that meets the write of a unit taken or
    /// given back fails - or fails that write.
    /// </summary>
    public static (IReadOnlyList<SlotEntry> Slots, IReadOnlyList<RoomClaim> Claims) ReadWhileAsked(string record)
        => (MachineWideFile.Update(Path.GetFullPath(record), MachineWideFile.Window, () => Read(record)),
            MachineWideFile.Update(Path.GetFullPath(HeavyLegSlots.RoomPathFor(record)), MachineWideFile.Window, () => ReadClaims(record)));
}
