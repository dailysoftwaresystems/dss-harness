using System.Globalization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// What tells one process from the next holder of its id. This is what a lock and a log claim record,
/// and the one rule it must keep is that no wall clock is in it: a host this tool serves steps its
/// clock forward by about 25 seconds every few seconds, and a stamp that moved with it would make
/// every live holder on that host read as dead at once.
/// </summary>
public sealed class ProcessIdentityTests
{
    [Fact]
    public void ThisProcess_IsAliveUnderItsOwnStamp()
    {
        var identity = Identity();

        Assert.True(identity.IsAlive(identity.CurrentId, identity.Current));
    }

    [Fact]
    public void ThisProcessesId_UnderAnotherStamp_IsNotThisProcess()
    {
        // A recycled id: something live carries it, and it is not what recorded the stamp.
        var identity = Identity();

        Assert.False(identity.IsAlive(identity.CurrentId, "a-stamp-no-process-here-carries"));
    }

    [Fact]
    public void AnIdNothingCarries_IsNotAlive()
    {
        Assert.False(Identity().IsAlive(int.MaxValue - 1, "anything at all"));
    }

    /// <summary>
    /// An id recorded by something that could not be told apart from a later holder of it is read as
    /// alive while anything carries it: taking what a live process holds is the worse mistake.
    /// </summary>
    [Fact]
    public void AnIdRecordedWithNoStamp_IsReadAsAlive_WhileSomethingCarriesIt()
    {
        var identity = Identity();

        Assert.True(identity.IsAlive(identity.CurrentId, null));
        Assert.False(identity.IsAlive(int.MaxValue - 1, null));
    }

    /// <summary>
    /// The regression this class exists for. Linux publishes a start time in ticks since boot, and
    /// the wall clock cannot move those. Adding a boot time to them — which is what both the process
    /// table and .NET's own <c>Process.StartTime</c> once did here, and what <c>ps lstart</c> still
    /// does — puts the clock back in and hands every live process a new identity when it steps.
    /// </summary>
    [Fact]
    public void OnLinux_TheStampIsBootAndTicks_WithNoClockInIt()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only Linux publishes ticks since boot; see the start-time test.");

        var stat = File.ReadAllText($"/proc/{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}/stat");
        var ticks = ProcStat.Number(ProcStat.FieldsAfterName(stat), ProcStat.StartTicksField);
        var boot = File.ReadAllText(ProcStat.BootIdPath).Trim();

        Assert.NotNull(ticks);
        Assert.Equal($"{boot}:{ticks!.Value.ToString(CultureInfo.InvariantCulture)}", Identity().Current, StringComparer.Ordinal);
    }

    /// <summary>
    /// Windows and macOS record the start time once, when the process is created, and never work it
    /// out again — so it is already clock-proof. It is kept as the ticks the runtime hands it over in,
    /// never turned back into an instant, so a stamp an earlier build recorded is still read as this
    /// build's; a time zone that changed since is the next test's.
    /// </summary>
    [Fact]
    public void OffLinux_TheStampIsTheRecordedStartTicks_WithNoConversion()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Linux works its start time out from boot; see the boot-and-ticks test.");

        using var current = System.Diagnostics.Process.GetCurrentProcess();

        Assert.Equal(
            current.StartTime.Ticks.ToString(CultureInfo.InvariantCulture),
            Identity().Current,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// A machine whose time zone changed - a laptop set by where it is - reads a live process's start in its new zone,
    /// apart from the one recorded by whole quarter hours; that is still the process that recorded it, and a holder it
    /// names is never taken for one that has gone. A start apart by anything else is another process's.
    /// </summary>
    [Fact]
    public void OffLinux_AStartReadUnderAnotherTimeZone_IsStillThisProcess()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Linux's stamp holds no time of day; see the boot-and-ticks test.");

        var identity = Identity();
        var ticks = long.Parse(identity.Current!, CultureInfo.InvariantCulture);

        Assert.True(identity.IsAlive(Environment.ProcessId, (ticks - TimeSpan.FromHours(3).Ticks).ToString(CultureInfo.InvariantCulture)));
        Assert.True(identity.IsAlive(Environment.ProcessId, (ticks + TimeSpan.FromMinutes(345).Ticks).ToString(CultureInfo.InvariantCulture)));
        Assert.False(identity.IsAlive(Environment.ProcessId, (ticks - TimeSpan.FromHours(3).Ticks + 1).ToString(CultureInfo.InvariantCulture)));
        Assert.False(identity.IsAlive(Environment.ProcessId, (ticks - TimeSpan.FromHours(27).Ticks).ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void TheStamp_IsTheSameEveryTimeItIsRead()
    {
        var identity = Identity();
        var first = identity.Current;

        Assert.Equal(first, Identity().Current, StringComparer.Ordinal);
        Assert.Equal(first, identity.Current, StringComparer.Ordinal);
    }

    private static IProcessIdentity Identity() => new ProcessIdentity(new HostPlatform());
}
