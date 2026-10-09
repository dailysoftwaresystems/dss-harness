using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Secrets;

namespace RepoHarness.Tests;

/// <summary>Gives this machine back the memory WSL's virtual machine holds as clean page cache.</summary>
public sealed class WslPageCacheTests
{
    /// <summary>What the drop prints, measured: the distribution's cache before and after, in kibibytes.</summary>
    private const string Printed = "Cached:         24249804 kB\nCached:          1346724 kB\n";

    /// <summary>
    /// The cache is dropped as root in the first distribution a host reaches that is running - every distribution runs in
    /// the one virtual machine - by the command that writes what is pending first, and the line says through which host
    /// and how much it dropped.
    /// </summary>
    [Fact]
    public async Task TheCache_IsDroppedAsRootInTheFirstRunningDistributionAHostReaches_SayingHowMuch()
    {
        var hosts = new ScriptedHostCommands((_, _) => HostResults.Ok(Printed))
        {
            RunningWslDistributions = () => HostResults.Ok("docker-desktop\r\nubuntu-24.04\r\n"),
        };

        var drop = await DropAsync(hosts, PlatformId.Windows, ("stopped", "Debian"), ("ubuntu", "Ubuntu-24.04"));

        var (connection, command) = Assert.Single(hosts.Calls);
        Assert.Equal((HostId.Wsl("ubuntu"), "Ubuntu-24.04"), (connection.Host, connection.Distribution));
        Assert.True(command.AsRoot);
        Assert.Equal("sh", command.Program);
        Assert.Equal(["-c", "grep '^Cached:' /proc/meminfo; sync; echo 1 > /proc/sys/vm/drop_caches && grep '^Cached:' /proc/meminfo"], command.Arguments);
        Assert.NotNull(drop);
        Assert.True(drop.Done);
        Assert.Equal("WSL's page cache was dropped as root in wsl ubuntu, 21.8 GiB of it", drop.Said);
    }

    /// <summary>
    /// Nothing is dropped, and nothing said, where there is nothing to drop: this machine is not Windows, no host reaches a
    /// distribution - none is declared, or its item cannot be read - or none that one reaches is running, and a stopped
    /// one, which holds no cache, is never started for it. Only where a host reaches one is WSL asked which run.
    /// </summary>
    [Theory]
    [InlineData("linux", false)]
    [InlineData("no host", false)]
    [InlineData("unread item", false)]
    [InlineData("none running", true)]
    public async Task NothingIsDropped_WhereThereIsNothingToDrop(string machine, bool asked)
    {
        var listings = 0;
        var hosts = new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command))
        {
            RunningWslDistributions = () =>
            {
                listings++;
                return HostResults.Ok("docker-desktop\n");
            },
        };

        var platform = machine == "linux" ? PlatformId.Linux : PlatformId.Windows;

        var drop = machine switch
        {
            "no host" => await DropAsync(hosts, platform),
            "unread item" => await DropAsync(hosts, platform, ("ubuntu", null)),
            _ => await DropAsync(hosts, platform, ("ubuntu", "Ubuntu-24.04")),
        };

        Assert.Null(drop);
        Assert.Empty(hosts.Calls);
        Assert.Equal(asked ? 1 : 0, listings);
    }

    /// <summary>
    /// A drop that could not be made says why, and is no drop: WSL would not say which distributions run, or could not be
    /// reached, or the drop failed or gave no answer in time. A drop whose printout says nothing of how much was cached is
    /// still a drop, and says only that.
    /// </summary>
    [Theory]
    [InlineData("listing failed", false, "WSL's page cache was not dropped: WSL did not say which distributions run (exit 1): Error code: Wsl/Service/E_UNEXPECTED")]
    [InlineData("WSL unreachable", false, "WSL's page cache was not dropped: wsl ubuntu could not be reached: 'wsl' could not be started: The file cannot be accessed by the system.")]
    [InlineData("drop failed", false, "WSL's page cache could not be dropped as root in wsl ubuntu (exit 2): sh: 1: cannot create /proc/sys/vm/drop_caches: Read-only file system")]
    [InlineData("drop timed out", false, "WSL's page cache could not be dropped as root in wsl ubuntu: there was no answer within 120 seconds")]
    [InlineData("nothing printed", true, "WSL's page cache was dropped as root in wsl ubuntu")]
    public async Task ADropThatCouldNotBeMade_SaysWhy_AndIsNoDrop(string failure, bool done, string said)
    {
        var hosts = new ScriptedHostCommands((connection, _) => failure switch
        {
            "WSL unreachable" => throw HostResults.TransportWouldNotStart(connection.Host),
            "drop failed" => HostResults.Failed(2, "sh: 1: cannot create /proc/sys/vm/drop_caches: Read-only file system\n"),
            "drop timed out" => new ProcessResult(-1, string.Empty, string.Empty, TimeSpan.FromMinutes(2), TimedOut: true),
            _ => HostResults.Ok(string.Empty),
        })
        {
            RunningWslDistributions = () => failure == "listing failed"
                ? HostResults.Failed(1, "Error code: Wsl/Service/E_UNEXPECTED\n")
                : HostResults.Ok("Ubuntu-24.04\n"),
        };

        var drop = await DropAsync(hosts, PlatformId.Windows, ("ubuntu", "Ubuntu-24.04"));

        Assert.NotNull(drop);
        Assert.Equal((done, said), (drop.Done, drop.Said));
    }

    /// <summary>
    /// How much was dropped is what was cached before less what was after, as the distribution printed both; a printout
    /// that does not say both says nothing of it, and a cache that grew meanwhile dropped none.
    /// </summary>
    [Theory]
    [InlineData(Printed, 22903080L * 1024)]
    [InlineData("Cached:  100 kB\nCached:  300 kB\n", 0L)]
    [InlineData("Cached:  100 kB\n", null)]
    [InlineData("Cached:  lots kB\nCached:  1 kB\n", null)]
    [InlineData("", null)]
    public void HowMuchWasDropped_IsWhatWasCachedBeforeLessWhatWasAfter(string printed, long? bytes)
        => Assert.Equal(bytes, WslPageCache.DroppedBytes(printed));

    /// <summary>
    /// Drops the cache over <paramref name="hosts"/>, on a machine running <paramref name="platform"/>, for a repository
    /// declaring a WSL host for each of <paramref name="declared"/>, in order, whose item names its distribution - or cannot
    /// be read, where it names none.
    /// </summary>
    private static Task<PageCacheDrop?> DropAsync(ScriptedHostCommands hosts, PlatformId platform, params (string Host, string? Distribution)[] declared)
    {
        var config = new HarnessConfig();
        var secrets = Substitute.For<IHostSecretsStore>();

        foreach (var (host, distribution) in declared)
        {
            config.Hosts.Wsl[host] = new WslHostConfig { RepositoryPath = "~/repo" };
            secrets.ReadWslItem(Arg.Any<HarnessLayout>(), host).Returns(
                distribution is null
                    ? new SecretsRead<WslItem>(null, $"'.harness-config/wslDistros/{host}/.env' could not be read")
                    : new SecretsRead<WslItem>(new WslItem { Name = host, Distribution = distribution }, string.Empty));
        }

        return new WslPageCache(secrets, hosts, HostDoubles.Platform(platform))
            .DropAsync(new HarnessContext(new HarnessLayout(TestHost.TemporaryRoot, TestHost.TemporaryRoot), config), TestContext.Current.CancellationToken);
    }
}
