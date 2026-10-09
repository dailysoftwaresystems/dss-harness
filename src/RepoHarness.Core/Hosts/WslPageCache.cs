using System.Globalization;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Secrets;

namespace RepoHarness.Core.Hosts;

/// <summary>What asking WSL's virtual machine to drop its page cache came to: dropped, or why it was not.</summary>
public sealed record PageCacheDrop
{
    private PageCacheDrop(bool done, string said)
    {
        Done = done;
        Said = said;
    }

    /// <summary>
    /// How long what a drop gives back takes to reach this machine's count: measured, WSL handed it back in two waves, a
    /// quarter of it within 8 seconds and the rest 38 to 47 seconds after the drop, and nothing after.
    /// </summary>
    public static readonly TimeSpan HandedBackWithin = TimeSpan.FromMinutes(1);

    /// <summary>Whether the cache was dropped.</summary>
    public bool Done { get; }

    /// <summary>
    /// What was dropped, and as root in which host - <c>WSL's page cache was dropped as root in wsl Ubuntu, 22.9 GiB of
    /// it</c> - or why nothing was.
    /// </summary>
    public string Said { get; }

    /// <summary>The cache dropped as root in <paramref name="through"/>, <paramref name="bytes"/> of it where the distribution said.</summary>
    public static PageCacheDrop Dropped(HostId through, long? bytes)
        => new(true, $"WSL's page cache was dropped as root in {through}" + (bytes is { } dropped ? $", {DiskSpace.Size(dropped)} of it" : string.Empty));

    /// <summary>Nothing dropped, for <paramref name="why"/>, which says so.</summary>
    public static PageCacheDrop NotDropped(string why) => new(false, why);
}

/// <summary>
/// Gives this machine back what WSL's virtual machine holds as clean page cache: the files its distributions read and
/// wrote, which it keeps until it has idled for minutes, and which this machine counts all the while as committed - the
/// memory its heavy legs are admitted by.
/// </summary>
public interface IWslPageCache
{
    /// <summary>
    /// Has WSL's virtual machine drop its clean page cache, as root in the first distribution a host of
    /// <paramref name="context"/> reaches that is running - every distribution runs in the one virtual machine, under one
    /// kernel - and says how much it dropped, or why nothing was: a host whose item cannot be read, where no other reaches a
    /// distribution, among the reasons; <see langword="null"/> where there was nothing to drop: this machine is not Windows,
    /// it declares no WSL host, or no distribution one reaches runs, and a stopped one holds nothing.
    /// </summary>
    /// <param name="context">The repository, whose WSL hosts name the distributions this tool may run a command in.</param>
    /// <param name="cancellationToken">Stops the drop.</param>
    Task<PageCacheDrop?> DropAsync(HarnessContext context, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IWslPageCache"/>
/// <remarks>
/// Measured on a Windows machine whose virtual machine held 24.2 GiB of cache: the drop took 3 seconds and left 1.3 GiB, and
/// this machine's commit fell from 93.8 GiB to 73.9 GiB within 47 seconds - 72.1 GiB with the virtual machine idle. WSL's own
/// reclaim drops the cache only once the virtual machine has idled for minutes, which no machine running legs there does.
/// Never in a distribution that is not running: starting one to drop a cache would take more memory than it gives back.
/// </remarks>
/// <param name="secrets">Reads which distribution each WSL host reaches.</param>
/// <param name="hostCommands">Asks WSL which distributions run, and drops the cache in one.</param>
/// <param name="platform">Says whether this machine is Windows, where alone WSL runs.</param>
public sealed class WslPageCache(IHostSecretsStore secrets, IHostCommandRunner hostCommands, IHostPlatform platform) : IWslPageCache
{
    /// <summary>
    /// What drops the cache, as one shell command: what is written and not yet on disk is written first, so it is clean and
    /// dropped too, and what the distribution counts as cached is printed before and after, in kibibytes.
    /// </summary>
    public const string DropCommand = "grep '^Cached:' /proc/meminfo; sync; echo 1 > /proc/sys/vm/drop_caches && grep '^Cached:' /proc/meminfo";

    /// <summary>How long WSL is given to say which distributions run.</summary>
    private static readonly TimeSpan ListBudget = TimeSpan.FromSeconds(30);

    /// <summary>How long the drop is given: what <c>sync</c> writes first may take a while under a build writing hard.</summary>
    private static readonly TimeSpan DropBudget = TimeSpan.FromMinutes(2);

    private readonly IHostSecretsStore _secrets = secrets;
    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IHostPlatform _platform = platform;

    public async Task<PageCacheDrop?> DropAsync(HarnessContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_platform.Current != PlatformId.Windows)
        {
            return null;
        }

        // Each host's distribution, read from its own item as connecting to it reads it: a host whose item cannot be read
        // reaches nothing - its legs cannot run either - and is said where no other host reaches one, so a leg the cache
        // keeps waiting is told why none was dropped.
        var items = context.Config.Hosts.Wsl.Keys
            .Select(name => (Host: HostId.Wsl(name), Read: _secrets.ReadWslItem(context.Layout, name)))
            .ToList();
        var reached = items
            .Select(each => (each.Host, Distribution: each.Read.Item?.Distribution))
            .Where(each => each.Distribution is { Length: > 0 })
            .ToList();

        if (reached.Count == 0)
        {
            return items.FirstOrDefault(each => each.Read.Item is null) is { Read: not null } unread
                ? PageCacheDrop.NotDropped($"WSL's page cache was not dropped: {unread.Host}: {unread.Read.Problem}")
                : null;
        }

        ProcessResult listed;

        try
        {
            listed = await _hostCommands.ListRunningWslDistributionsAsync(ListBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return PageCacheDrop.NotDropped($"WSL's page cache was not dropped: {ex.Message}");
        }

        if (listed.TimedOut || !listed.Succeeded)
        {
            return PageCacheDrop.NotDropped($"WSL's page cache was not dropped: {HostProbes.Failure("WSL did not say which distributions run", listed)}");
        }

        var running = listed.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (reached.FirstOrDefault(each => running.Contains(each.Distribution!)) is not { Distribution: { } distribution } through)
        {
            return null;
        }

        var connection = new HostConnection { Host = through.Host, Distribution = distribution };
        ProcessResult dropped;

        try
        {
            dropped = await _hostCommands
                .RunAsync(connection, new HostCommand { Program = "sh", Arguments = ["-c", DropCommand], AsRoot = true, StandardInput = string.Empty, Timeout = DropBudget }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return PageCacheDrop.NotDropped($"WSL's page cache was not dropped: {ex.Message}");
        }

        return dropped.Succeeded && !dropped.TimedOut
            ? PageCacheDrop.Dropped(through.Host, DroppedBytes(dropped.StandardOutput))
            : PageCacheDrop.NotDropped(HostProbes.Failure($"WSL's page cache could not be dropped as root in {through.Host}", dropped, connection));
    }

    /// <summary>
    /// How much of the cache <paramref name="printed"/> - what <see cref="DropCommand"/> prints - says was dropped, or
    /// <see langword="null"/> where it does not say both how much was cached before and how much after.
    /// </summary>
    internal static long? DroppedBytes(string printed)
    {
        var cached = printed
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("Cached:", StringComparison.Ordinal))
            .Select(line => line["Cached:".Length..].Trim().Split(' ')[0])
            .Select(kibibytes => long.TryParse(kibibytes, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : (long?)null)
            .ToList();

        return cached is [{ } before, { } after] ? Math.Max(0, before - after) * 1024 : null;
    }
}
