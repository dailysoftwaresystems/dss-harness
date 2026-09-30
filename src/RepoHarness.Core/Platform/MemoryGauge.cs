using System.Globalization;
using System.Runtime.InteropServices;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Platform;

/// <summary>How much of a machine's memory is in use, as its operating system counts it.</summary>
/// <param name="Percent">The share in use, from 0 to 100.</param>
/// <param name="Figure">What it was counted from, as a reader would check it: <c>commit 81 GiB of 113.7 GiB</c>.</param>
public sealed record MemoryReading(double Percent, string Figure)
{
    /// <summary>The share to a tenth, as every line and document gives it, a half rounded up.</summary>
    public double Rounded => Math.Round(Percent, 1, MidpointRounding.AwayFromZero);

    /// <summary>The reading as a line says it: <c>71.2% in use (commit 81 GiB of 113.7 GiB)</c>.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"{Rounded:0.0}% in use ({Figure})");
}

/// <summary>Reads how much of this machine's memory is in use.</summary>
public interface IMemoryGauge
{
    /// <summary>The memory in use now, or why it could not be read.</summary>
    (MemoryReading? Reading, string? Unmeasured) Read();
}

/// <inheritdoc cref="IMemoryGauge"/>
/// <remarks>
/// <para>
/// Each system's own count of what it can no longer give without failing or swapping to death, as a share of what it
/// could give at all. On Windows that is the commit charge against the commit limit - what every process has been
/// promised, which Windows refuses to promise past its limit - read with <c>GetPerformanceInfo</c>, which counts the
/// whole machine: <c>GlobalMemoryStatusEx</c> gives the same two figures, but only as far as a job object the asking
/// process runs in allows. It agreed with <c>Win32_OperatingSystem</c> within 1% on the machine it was measured on. The
/// limit grows with the page file, so the same charge reads lower once Windows has grown it. A WSL distribution's
/// memory is this machine's commit: its legs are admitted by this machine's count, never by the distribution's own -
/// which, capped by its own configuration, can run out while this machine's count still shows room.
/// </para>
/// <para>
/// On Linux it is what the kernel counts as not available - <c>MemTotal</c> less <c>MemAvailable</c> - rather than
/// <c>Committed_AS</c> against <c>CommitLimit</c>, which under the default overcommit routinely stands above 100% on a
/// healthy machine and would admit nothing. On macOS it is 100 less the free share in <c>kern.memorystatus_level</c>,
/// which <c>memory_pressure</c> reports as free.
/// </para>
/// </remarks>
/// <param name="platform">Says which system this is.</param>
/// <param name="fileSystem">Reads what Linux publishes of its memory.</param>
public sealed class MemoryGauge(IHostPlatform platform, IFileSystem fileSystem) : IMemoryGauge
{
    private const string Meminfo = "/proc/meminfo";

    private readonly IHostPlatform _platform = platform;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <inheritdoc/>
    public (MemoryReading? Reading, string? Unmeasured) Read()
    {
        try
        {
            return _platform.Current switch
            {
                PlatformId.Windows => Windows(),
                PlatformId.Linux => FromMeminfo(_fileSystem.ReadAllText(Meminfo)),
                _ => Mac(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return (null, ex.Message.TrimEnd('.'));
        }
    }

    /// <summary>The commit charge against the commit limit, from what Windows says of both.</summary>
    /// <param name="chargedBytes">The commit charge: what every process has been promised.</param>
    /// <param name="limitBytes">The commit limit: what the memory and the page file can give together.</param>
    internal static (MemoryReading? Reading, string? Unmeasured) FromCommit(ulong chargedBytes, ulong limitBytes)
    {
        if (limitBytes == 0 || chargedBytes > limitBytes)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"Windows gave a commit charge of {chargedBytes} bytes against a limit of {limitBytes}"));
        }

        return (new MemoryReading(100.0 * chargedBytes / limitBytes, $"commit {Size(chargedBytes)} of {Size(limitBytes)}"), null);
    }

    /// <summary>What the kernel counts as not available, from <c>/proc/meminfo</c>.</summary>
    /// <param name="meminfo">The file's text.</param>
    internal static (MemoryReading? Reading, string? Unmeasured) FromMeminfo(string meminfo)
    {
        ArgumentNullException.ThrowIfNull(meminfo);

        var total = Kibibytes(meminfo, "MemTotal");
        var available = Kibibytes(meminfo, "MemAvailable");

        // MemAvailable is the kernel's own estimate of what a new program can have without swapping, which nothing
        // else in the file adds up to; a kernel too old to publish it cannot be read by this count.
        if (total is not > 0 || available is null || available > total)
        {
            return (null, $"{Meminfo} gives no MemTotal and MemAvailable to count by");
        }

        var used = total.Value - available.Value;

        return (new MemoryReading(100.0 * used / total.Value, $"{DiskSpace.Size(used * 1024)} of {DiskSpace.Size(total.Value * 1024)} not available"), null);
    }

    /// <summary>What the kernel counts as not free, from its free share and the memory the machine has.</summary>
    /// <param name="freePercent">The free share, as <c>kern.memorystatus_level</c> gives it.</param>
    /// <param name="totalBytes">The memory the machine has, as <c>hw.memsize</c> gives it.</param>
    internal static (MemoryReading? Reading, string? Unmeasured) FromFreePercent(int freePercent, long totalBytes)
    {
        if (freePercent is < 0 or > 100)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"kern.memorystatus_level gave {freePercent}, which is no share"));
        }

        var figure = totalBytes > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{freePercent}% of {DiskSpace.Size(totalBytes)} free")
            : string.Create(CultureInfo.InvariantCulture, $"{freePercent}% free");

        return (new MemoryReading(100 - freePercent, figure), null);
    }

    /// <summary>A count of bytes as a line gives it, one too large to name as a size named as the largest.</summary>
    private static string Size(ulong bytes) => DiskSpace.Size((long)Math.Min(bytes, long.MaxValue));

    /// <summary>The value, in KiB, a line of <c>/proc/meminfo</c> gives <paramref name="key"/>, or <see langword="null"/>.</summary>
    private static long? Kibibytes(string meminfo, string key)
    {
        foreach (var line in meminfo.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);

            if (colon <= 0 || !line.AsSpan(0, colon).SequenceEqual(key))
            {
                continue;
            }

            var value = line[(colon + 1)..].Trim();
            var number = value.EndsWith(" kB", StringComparison.Ordinal) ? value[..^3].Trim() : value;

            return long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var kibibytes) ? kibibytes : null;
        }

        return null;
    }

    private static (MemoryReading? Reading, string? Unmeasured) Windows()
    {
        var size = (uint)Marshal.SizeOf<PerformanceInformation>();

        if (!GetPerformanceInfo(out var information, size))
        {
            return (null, $"GetPerformanceInfo failed with error {Marshal.GetLastPInvokeError()}");
        }

        var page = (ulong)information.PageSize;

        return FromCommit((ulong)information.CommitTotal * page, (ulong)information.CommitLimit * page);
    }

    private static (MemoryReading? Reading, string? Unmeasured) Mac()
    {
        int level = 0;
        var levelLength = (nint)sizeof(int);

        if (SysctlByName("kern.memorystatus_level", ref level, ref levelLength, IntPtr.Zero, 0) != 0)
        {
            return (null, $"sysctl kern.memorystatus_level failed with error {Marshal.GetLastPInvokeError()}");
        }

        long total = 0;
        var totalLength = (nint)sizeof(long);

        // The size only names the figure; a machine that will not say it is still read by its free share.
        return FromFreePercent(level, SysctlByName("hw.memsize", ref total, ref totalLength, IntPtr.Zero, 0) == 0 ? total : 0);
    }

    /// <summary><c>PERFORMANCE_INFORMATION</c>: the machine's commit, in pages, and the size of a page.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation information, uint size);

    [DllImport("libc", EntryPoint = "sysctlbyname", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SysctlByName([MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref int value, ref nint length, IntPtr newValue, nint newLength);

    [DllImport("libc", EntryPoint = "sysctlbyname", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SysctlByName([MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref long value, ref nint length, IntPtr newValue, nint newLength);
}
