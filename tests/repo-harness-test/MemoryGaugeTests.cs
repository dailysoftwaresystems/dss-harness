using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// How much of a machine's memory is in use, as each system counts it: on Windows the commit charge against the
/// commit limit, which the consumer measured agreeing with Win32_OperatingSystem within 1%; on Linux what the kernel
/// counts as not available; on macOS what it counts as not free. Read on this machine too, so each system's own call is
/// proved on the CI leg that runs it.
/// </summary>
public sealed class MemoryGaugeTests
{
    private const long Gibibyte = 1L << 30;

    /// <summary>The commit charge against the commit limit: what the consumer's machine stood at when the process that started its builds died.</summary>
    [Fact]
    public void OnWindows_TheCommitChargeIsCountedAgainstTheCommitLimit()
    {
        var (reading, unmeasured) = MemoryGauge.FromCommit((ulong)(81 * Gibibyte), (ulong)(113.7 * Gibibyte));

        Assert.Null(unmeasured);
        Assert.Equal(71.2, reading!.Rounded);
        Assert.Equal("71.2% in use (commit 81 GiB of 113.7 GiB)", reading.Describe());
    }

    /// <summary>
    /// What the kernel counts as available is what a new program can have without swapping, which no other line of the
    /// file adds up to; a kernel too old to give it cannot be read by this count, and says so.
    /// </summary>
    [Fact]
    public void OnLinux_WhatIsNotAvailableIsCounted_AndAKernelGivingNoneIsSaid()
    {
        var (reading, unmeasured) = MemoryGauge.FromMeminfo(
            "MemTotal:       16384000 kB\nMemFree:          409600 kB\nMemAvailable:    4096000 kB\nBuffers:           10240 kB\n");

        Assert.Null(unmeasured);
        Assert.Equal(75.0, reading!.Rounded);
        Assert.Equal("75.0% in use (11.7 GiB of 15.6 GiB not available)", reading.Describe());

        var (none, why) = MemoryGauge.FromMeminfo("MemTotal:       16384000 kB\nMemFree:          409600 kB\n");

        Assert.Null(none);
        Assert.Equal("/proc/meminfo gives no MemTotal and MemAvailable to count by", why);
    }

    /// <summary>
    /// What the kernel counts as not free, from the free share memory_pressure reports; a machine that will not say how
    /// much memory it has is still read by that share.
    /// </summary>
    [Fact]
    public void OnMacOs_WhatIsNotFreeIsCounted()
    {
        var (reading, unmeasured) = MemoryGauge.FromFreePercent(38, 16 * Gibibyte);

        Assert.Null(unmeasured);
        Assert.Equal("62.0% in use (38% of 16 GiB free)", reading!.Describe());
        Assert.Equal("62.0% in use (38% free)", MemoryGauge.FromFreePercent(38, 0).Reading!.Describe());
    }

    /// <summary>
    /// A count that cannot be right is no reading at all, and says so: never a share above 100 that would admit nothing
    /// for ever, nor one below 0 that would admit everything, nor a machine with nothing in use.
    /// </summary>
    [Fact]
    public void ACountThatCannotBeRight_IsNoReading()
    {
        (MemoryReading? Reading, string? Unmeasured)[] counts =
        [
            MemoryGauge.FromCommit(0, 0),
            MemoryGauge.FromCommit(200, 100),
            MemoryGauge.FromMeminfo("MemTotal:       0 kB\nMemAvailable:    0 kB\n"),
            MemoryGauge.FromMeminfo("MemTotal:       1024 kB\nMemAvailable:    2048 kB\n"),
            MemoryGauge.FromFreePercent(-1, 16 * Gibibyte),
            MemoryGauge.FromFreePercent(101, 16 * Gibibyte),
        ];

        Assert.All(counts, count =>
        {
            Assert.Null(count.Reading);
            Assert.False(string.IsNullOrWhiteSpace(count.Unmeasured));
        });
    }

    /// <summary>A count the system would not give is no reading, and says why: the leg is then let start on its slot alone.</summary>
    [Fact]
    public void ACountTheSystemWouldNotGive_IsNoReading_SayingWhy()
    {
        var harness = new HarnessFactory();
        var gauge = new MemoryGauge(HostDoubles.Platform(PlatformId.Linux), new UnreadableMeminfo(harness.FileSystem));

        var (reading, unmeasured) = gauge.Read();

        Assert.Null(reading);
        Assert.Equal("Access to the path '/proc/meminfo' is denied", unmeasured);
    }

    /// <summary>This machine's own count is read, whichever system it is.</summary>
    [Fact]
    public void ThisMachinesOwnCount_IsRead()
    {
        var harness = new HarnessFactory();
        var (reading, unmeasured) = new MemoryGauge(harness.Platform, harness.FileSystem).Read();

        Assert.Null(unmeasured);
        Assert.InRange(reading!.Percent, 0, 100);
        Assert.NotEmpty(reading.Figure);
    }

    /// <summary>The real file system, except that what Linux publishes of its memory may not be read.</summary>
    private sealed class UnreadableMeminfo(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public override string ReadAllText(string path)
            => path == "/proc/meminfo" ? throw new UnauthorizedAccessException("Access to the path '/proc/meminfo' is denied.") : base.ReadAllText(path);
    }
}
