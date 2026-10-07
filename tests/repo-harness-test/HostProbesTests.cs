using NSubstitute;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// What programs on a host print is read before a leg is placed there. A reader that guessed at text
/// it did not recognise would place a leg on a host that cannot run it.
/// </summary>
public sealed class HostProbesTests
{
    [Theory]
    [InlineData("Linux x86_64\n", "linux", "x86_64")]
    [InlineData("Linux aarch64", "linux", "arm64")]
    [InlineData("Darwin arm64\n", "macos", "arm64")]
    public void ReadUname_TranslatesTheSystemAndTheMachine(string output, string os, string processor)
    {
        var (actualOs, actualProcessor) = HostProbes.ReadUname(output);

        Assert.Equal(os, actualOs);
        Assert.Equal(processor, actualProcessor);
    }

    [Fact]
    public void ReadUname_ReadsNothing_FromTextThatIsNotItsOutput()
    {
        var (os, processor) = HostProbes.ReadUname("uname: command not found");

        Assert.Null(os);
        Assert.Null(processor);
    }

    [Fact]
    public void ReadSdks_ReadsEverySdk_AndSkipsABannerBeforeThem()
    {
        const string Output = "Welcome to .NET!\n---------------------\n8.0.414 [/usr/lib/dotnet/sdk]\n10.0.100 [/usr/lib/dotnet/sdk]\n";

        var sdks = HostProbes.ReadSdks(Output);

        Assert.Equal(["8.0.414", "10.0.100"], sdks.Select(sdk => sdk.Version));
        Assert.Equal([8, 10], sdks.Select(sdk => sdk.Major));
        Assert.All(sdks, sdk => Assert.False(sdk.OnWindows));
    }

    [Fact]
    public void ReadSdks_TellsAWindowsInstallation_ByItsPath()
    {
        var sdk = Assert.Single(HostProbes.ReadSdks("10.0.401 [C:\\Program Files\\dotnet\\sdk]\r\n"));

        Assert.True(sdk.OnWindows);
        Assert.Equal(10, sdk.Major);
    }

    [Fact]
    public void TryReadToolVersion_FindsTheTool_WhateverTheCaseOfItsId()
    {
        const string Output = """
            {"version":1,"data":[{"packageId":"dotnet-dump","version":"9.0.1","commands":["dotnet-dump"]},{"packageId":"dssharness","version":"0.2.0-beta","commands":["DssHarness"]}]}
            """;

        Assert.True(HostProbes.TryReadToolVersion(Output, "DssHarness", out var version));
        Assert.Equal("0.2.0-beta", version);
    }

    [Fact]
    public void TryReadToolVersion_ReadsATool_ThatIsNotInstalled()
    {
        Assert.True(HostProbes.TryReadToolVersion("""{"version":1,"data":[]}""", "DssHarness", out var version));
        Assert.Null(version);
    }

    [Fact]
    public void TryReadToolVersion_FindsTheDocument_AfterABanner()
    {
        const string Output = "Welcome to .NET!\n{\"version\":1,\"data\":[{\"packageId\":\"dssharness\",\"version\":\"1.0.0\"}]}";

        Assert.True(HostProbes.TryReadToolVersion(Output, "DssHarness", out var version));
        Assert.Equal("1.0.0", version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Tool list failed")]
    [InlineData("{ not json")]
    [InlineData("""{"version":1}""")]
    public void TryReadToolVersion_RefusesTextThatIsNotTheListing(string output)
    {
        Assert.False(HostProbes.TryReadToolVersion(output, "DssHarness", out _));
    }

    [Fact]
    public void IsMissingDistribution_ReadsTheErrorCode_WhateverLanguageTheSentenceIsIn()
    {
        // Measured on a Windows set to Portuguese: the sentence is translated, the code is not.
        Assert.True(HostProbes.IsMissingDistribution(
            "Não há distribuição com o nome fornecido.\nCódigo de erro: Wsl/Service/WSL_E_DISTRO_NOT_FOUND"));
        Assert.False(HostProbes.IsMissingDistribution("Linux x86_64"));
    }

    [Fact]
    public void IsMissingProgramInWsl_ReadsWhatWslPrints_ForAProgramTheDistributionLacks()
    {
        // Measured: what WSL prints when --exec names a program the distribution does not have.
        const string Output = "<3>WSL (708) ERROR: CreateProcessCommon:559: execvpe(dotnet) failed: No such file or directory";

        Assert.True(HostProbes.IsMissingProgramInWsl(Output, "dotnet"));
        Assert.False(HostProbes.IsMissingProgramInWsl(Output, "uname"));
    }

    [Theory]
    [InlineData("bash\nsshd\nDssHarness\n", true)]
    [InlineData("/Users/dev/.dotnet/tools/dssharness\n", true)]
    [InlineData("\"svchost.exe\",\"1234\",\"Services\",\"0\",\"10,000 K\"\r\n\"DssHarness.exe\",\"4321\",\"Console\",\"1\",\"50,000 K\"\r\n", true)]
    [InlineData("bash\nDssHarness-helper\nsshd\n", false)]
    [InlineData("\"svchost.exe\",\"1234\",\"Services\",\"0\",\"10,000 K\"\r\n", false)]
    public void ListsProcess_FindsTheTool_InEitherKindOfListing(string listing, bool expected)
    {
        Assert.Equal(expected, HostProbes.ListsProcess(listing, "DssHarness"));
    }

    [Fact]
    public void Excerpt_KeepsTheEndOfALongOutput_OnOneLine()
    {
        var output = string.Join("\n", Enumerable.Range(1, 200).Select(index => $"line {index}")) + "\nthe actual error";

        var excerpt = HostProbes.Excerpt(output);

        Assert.StartsWith("...", excerpt, StringComparison.Ordinal);
        Assert.EndsWith("the actual error", excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", excerpt, StringComparison.Ordinal);
    }

    /// <summary>
    /// ssh's own words for never having connected, as each client measured says them - a name that did not
    /// resolve, an address where nothing answered, one that refused - read out of whatever else it printed.
    /// What it fails over after the host answered is not among them: there the host was reached.
    /// </summary>
    [Theory]
    [InlineData("ssh: Could not resolve hostname mac.local: No such host is known. \r\n", "ssh: Could not resolve hostname mac.local: No such host is known.")]
    [InlineData("ssh: Could not resolve hostname nosuchhost.invalid: Name or service not known\n", "ssh: Could not resolve hostname nosuchhost.invalid: Name or service not known")]
    [InlineData("ssh: connect to host 192.0.2.10 port 22: Connection timed out\n", "ssh: connect to host 192.0.2.10 port 22: Connection timed out")]
    [InlineData("ssh: connect to host 127.0.0.1 port 1: Connection refused\n", "ssh: connect to host 127.0.0.1 port 1: Connection refused")]
    [InlineData("banner exchange: Connection to UNKNOWN port -1: Connection refused\r\n", "banner exchange: Connection to UNKNOWN port -1: Connection refused")]
    [InlineData("Pseudo-terminal will not be allocated because stdin is not a terminal.\nssh: connect to host fe80::1%12 port 22: Network is unreachable\n", "ssh: connect to host fe80::1%12 port 22: Network is unreachable")]
    [InlineData("Connection to vps.example closed by remote host.\n", null)]
    [InlineData("Host key verification failed.\n", null)]
    [InlineData("harness@vps.example: Permission denied (publickey).\n", null)]
    [InlineData("kex_exchange_identification: read: Connection reset by peer\n", null)]
    [InlineData("banner exchange: Connection to 192.0.2.10 port 22: invalid format\n", null)]
    [InlineData("Timeout, server vps.example not responding.\n", null)]
    [InlineData("the build said: ssh: connect to host 192.0.2.10 port 22: Connection refused\n", null)]
    [InlineData("", null)]
    public void NeverConnected_ReadsSshsOwnWordsForNeverHavingConnected_AndNothingElse(string standardError, string? line)
    {
        Assert.Equal(line, HostProbes.NeverConnected(standardError));
    }

    /// <summary>
    /// Only ssh failing to connect reads as a host that could not be reached: not the same words from a
    /// program that ran and exited otherwise, from one that ran out of time, from WSL, whose programs keep
    /// their own exit codes, or from a program this machine ran itself.
    /// </summary>
    [Theory]
    [InlineData("ssh", 255, false, true)]
    [InlineData("ssh", 1, false, false)]
    [InlineData("ssh", 255, true, false)]
    [InlineData("wsl", 255, false, false)]
    [InlineData("wsl", -1, false, false)]
    [InlineData(null, 255, false, false)]
    public void Unreached_IsSshFailingToConnect_AndNothingElse(string? transport, int exitCode, bool timedOut, bool unreached)
    {
        var result = new ProcessResult(exitCode, string.Empty, "ssh: Could not resolve hostname mac.local: No such host is known.\n", TimeSpan.Zero, timedOut);
        var through = transport switch
        {
            "ssh" => new HostConnection { Host = HostId.Ssh("mac") },
            "wsl" => new HostConnection { Host = HostId.Wsl("Example-Linux"), Distribution = "Example-Linux" },
            _ => null,
        };

        var said = HostProbes.Unreached(result, through);

        Assert.Equal(unreached ? "the host could not be reached: ssh said ssh: Could not resolve hostname mac.local: No such host is known." : null, said);
    }

    /// <summary>
    /// Where a connection is pinned, ssh names the address this machine resolved, and every line the harness
    /// relays names the host as the configuration declares it instead: a reason, a quotation and a raw line
    /// alike. An address this machine worked out is not the reader's to publish.
    /// </summary>
    [Fact]
    public void APinnedAddress_IsRelayedAsTheConfigurationDeclaresTheHost()
    {
        const string resolved = "198.51.100.7";
        var pinned = new HostConnection
        {
            Host = HostId.Ssh("mac"),
            Address = "mac.invalid",
            Pin = new SshPin(resolved, "mac.invalid"),
            Resolved = Learnt(resolved),
        };
        var timedOut = HostResults.Failed(255, $"ssh: connect to host {resolved} port 22: Connection timed out\n");

        Assert.Equal(
            "the host could not be reached: ssh said ssh: connect to host mac.invalid port 22: Connection timed out",
            HostProbes.Unreached(timedOut, pinned));
        Assert.DoesNotContain(resolved, HostProbes.NeverFinished("'build'", timedOut, pinned), StringComparison.Ordinal);
        Assert.DoesNotContain(resolved, HostProbes.Failure("'build'", timedOut, pinned), StringComparison.Ordinal);
        Assert.Equal(
            $"Connection to mac.invalid port 22 timed out",
            HostProbes.AsConfigured($"Connection to {resolved} port 22 timed out", pinned));
    }

    /// <summary>
    /// Only the address itself is rewritten, never a longer one that merely begins with it: a line the
    /// command's own work printed would otherwise name an address that never existed.
    /// </summary>
    [Theory]
    [InlineData("connecting to 10.0.0.50:8080", "connecting to 10.0.0.50:8080")]
    [InlineData("ssh: connect to host 10.0.0.5 port 22: Connection timed out", "ssh: connect to host mac.invalid port 22: Connection timed out")]
    [InlineData("at 10.0.0.5, not 10.0.0.51", "at mac.invalid, not 10.0.0.51")]
    public void ALongerAddressThatMerelyBeginsWithThePinned_IsLeftAlone(string said, string expected)
    {
        var pinned = new HostConnection
        {
            Host = HostId.Ssh("mac"),
            Address = "mac.invalid",
            Pin = new SshPin("10.0.0.5", "mac.invalid"),
            Resolved = Learnt("10.0.0.5"),
        };

        Assert.Equal(expected, HostProbes.AsConfigured(said, pinned));
    }

    /// <summary>
    /// Every address the host's name resolved to is written as the configuration declares the host, however ssh
    /// spells it and wherever it puts it, the pinned one or another of them. Measured: a refused key is
    /// "user@&lt;address&gt;: Permission denied", with ssh's colon after the address. Linux's ssh names a
    /// link-local address dialled as <c>%2</c> by its interface, <c>%eth0</c>. A connection whose pin was
    /// dropped lets ssh dial whichever address it chooses. ssh's debug lines put a port after the address, an
    /// IPv6 one unbracketed where the line's shape says where the port starts. And ifconfig sticks a label to the
    /// address's front. What surrounds the address stays as it was.
    /// </summary>
    [Theory]
    [InlineData("harness@192.0.2.10: Permission denied (publickey).", "harness@mac.invalid: Permission denied (publickey).")]
    [InlineData("harness@fe80::1c2b:3d4e:5f60:7182%12: Permission denied (publickey).", "harness@mac.invalid: Permission denied (publickey).")]
    [InlineData("Connection to fe80::1c2b:3d4e:5f60:7182%eth0 port 22 timed out", "Connection to mac.invalid port 22 timed out")]
    [InlineData("ssh: connect to host FE80:0:0:0:1C2B:3D4E:5F60:7182%12 port 22: Connection refused", "ssh: connect to host mac.invalid port 22: Connection refused")]
    [InlineData("Connection to fe80::1c2b:3d4e:5f60:7182 port 22 timed out", "Connection to mac.invalid port 22 timed out")]
    [InlineData("Connection reset by ::ffff:192.0.2.10 port 22", "Connection reset by mac.invalid port 22")]
    [InlineData("the build reached 192.0.2.10.", "the build reached mac.invalid.")]
    [InlineData("Connection to 192.0.2.10 port 22 timed out / Connection to 2001:db8::7 port 22 timed out", "Connection to mac.invalid port 22 timed out / Connection to mac.invalid port 22 timed out")]
    [InlineData("listening on fe80::1c2b:3d4e:5f60:7182%eth0.", "listening on mac.invalid.")]
    [InlineData("Connection to fe80::1c2b:3d4e:5f60:7182%eth0.100 port 22 timed out", "Connection to mac.invalid port 22 timed out")]
    [InlineData("debug1: Authenticating to 192.0.2.10:22 as 'harness'", "debug1: Authenticating to mac.invalid:22 as 'harness'")]
    [InlineData("debug1: Authenticating to fe80::1c2b:3d4e:5f60:7182%12:22 as 'harness'", "debug1: Authenticating to mac.invalid:22 as 'harness'")]
    [InlineData("debug1: Connecting to mac.invalid [fe80::1c2b:3d4e:5f60:7182%12] port 22.", "debug1: Connecting to mac.invalid [mac.invalid] port 22.")]
    [InlineData("a scope left empty: fe80::1c2b:3d4e:5f60:7182%", "a scope left empty: mac.invalid%")]
    [InlineData("Connection to 2001:db8:: port 22 timed out", "Connection to mac.invalid port 22 timed out")]
    [InlineData("harness@2001:db8::: Permission denied (publickey).", "harness@mac.invalid: Permission denied (publickey).")]
    [InlineData("inet addr:192.0.2.10  Bcast:192.0.2.255  Mask:255.255.255.0", "inet addr:mac.invalid  Bcast:192.0.2.255  Mask:255.255.255.0")]
    [InlineData("dead:192.0.2.10 and x:y:192.0.2.10", "dead:mac.invalid and x:y:mac.invalid")]
    [InlineData("debug1: Authenticating to 2001:db8::7:22 as 'harness'", "debug1: Authenticating to mac.invalid:22 as 'harness'")]
    [InlineData("listening on 192.0.2.10:8080 and fe80::1c2b:3d4e:5f60:7182%12:8080", "listening on mac.invalid:8080 and mac.invalid:8080")]
    public void EveryAddressTheNameResolvedTo_IsRelayedAsTheConfigurationDeclaresTheHost_HoweverSshSpellsIt(string said, string expected)
    {
        var resolved = new HostConnection
        {
            Host = HostId.Ssh("mac"),
            Address = "mac.invalid",
            Pin = new SshPin("192.0.2.10", "mac.invalid"),
            Resolved = Learnt("192.0.2.10", "fe80::1c2b:3d4e:5f60:7182%12", "2001:db8::7", "2001:db8::"),
        };

        Assert.Equal(expected, HostProbes.AsConfigured(said, resolved));
    }

    /// <summary>
    /// What only looks like one of the addresses is left as it is - another address, one beginning with it, and a
    /// version or a number that would parse as it: '192.0.522' and '3221225994' are both 192.0.2.10 to a parser - and so
    /// is one of them standing where no word of its own sets it apart: behind three labels, or before a URL's escape.
    /// </summary>
    [Theory]
    [InlineData("version 192.0.522 and 3221225994 bytes")]
    [InlineData("at 0300.0.2.10")]
    [InlineData("listening on fe80::1c2b:3d4e:5f60:7183%12 and ::1")]
    [InlineData("connecting to 192.0.2.100, then 192.0.2.1:8080")]
    [InlineData("std::fe80::1c2b and fe80::1c2b:3d4e:5f60:7182:9")]
    [InlineData("fetched http://192.0.2.10%2Fpath")]
    [InlineData("a neighbour at 2001:db8::7:22, and a card at 00:1a:2b:3c:4d:5e")]
    [InlineData("three labels stacked: a:b:c:192.0.2.10")]
    [InlineData("debug1: Authenticating to 2001:db8::8:22 as 'harness'")]
    public void WhatOnlyLooksLikeAnAddressTheNameResolvedTo_IsLeftAlone(string said)
    {
        var resolved = new HostConnection
        {
            Host = HostId.Ssh("mac"),
            Address = "mac.invalid",
            Resolved = Learnt("192.0.2.10", "fe80::1c2b:3d4e:5f60:7182%12", "2001:db8::7"),
        };

        Assert.Equal(said, HostProbes.AsConfigured(said, resolved));
    }

    /// <summary>
    /// A word a host prints is read in one pass however long it is: a line of a hundred thousand full stops, as a
    /// test runner prints one for each test, or of a hundred thousand labels, is left as it is at once.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task ALongWord_IsReadInOnePass()
    {
        var labels = string.Concat(Enumerable.Repeat("a:", 100_000));
        var said = new string('.', 200_000) + " 192.0.2.10 " + new string(':', 200_000) + " " + new string('a', 200_000) + " " + labels;
        var resolved = new HostConnection { Host = HostId.Ssh("mac"), Address = "mac.invalid", Resolved = Learnt("192.0.2.10") };

        var relayed = await Task.Run(() => HostProbes.AsConfigured(said, resolved), TestContext.Current.CancellationToken);

        Assert.Equal(new string('.', 200_000) + " mac.invalid " + new string(':', 200_000) + " " + new string('a', 200_000) + " " + labels, relayed);
    }

    /// <summary>
    /// A connection that was never pinned still has the addresses its name resolved to written as declared: ssh,
    /// looking the name up itself, names whichever it dialled.
    /// </summary>
    [Fact]
    public void AnUnpinnedConnection_StillHasTheAddressesItsNameResolvedToWrittenAsDeclared()
    {
        var unpinned = new HostConnection
        {
            Host = HostId.Ssh("mac"),
            Address = "mac.invalid",
            Resolved = Learnt("192.0.2.10", "fe80::1c2b:3d4e:5f60:7182%12"),
        };

        Assert.Equal(
            "the host could not be reached: ssh said Connection timed out during banner exchange / Connection to mac.invalid port 22 timed out",
            HostProbes.CouldNotReach(
                HostResults.Failed(255, "Connection timed out during banner exchange\nConnection to fe80::1c2b:3d4e:5f60:7182%12 port 22 timed out\n"),
                unpinned));
    }

    /// <summary>
    /// Nothing is rewritten where the connection knows of no address its name resolved to - none was learnt, as for a
    /// host reached through a jump host, or what was learnt is no address - or where there is no connection at all:
    /// those words are already the reader's own.
    /// </summary>
    [Fact]
    public void WhereNoAddressItsNameResolvedToIsKnown_SshsWordsAreRelayedAsTheyAre()
    {
        const string said = "ssh: connect to host mac.invalid port 22: Connection timed out";

        Assert.Equal(said, HostProbes.AsConfigured(said, null));
        Assert.Equal(said, HostProbes.AsConfigured(said, new HostConnection { Host = HostId.Ssh("mac"), Address = "mac.invalid" }));
        Assert.Equal(said, HostProbes.AsConfigured(said, new HostConnection { Host = HostId.Ssh("mac"), Address = "mac.invalid", Resolved = Learnt("mac.invalid") }));
    }

    /// <summary>
    /// A program whose end never came back is said as a host that could not be reached where ssh never
    /// connected, and otherwise as a program that may have run only in part, with what the connection said.
    /// </summary>
    [Theory]
    [InlineData("ssh: Could not resolve hostname mac.local: No such host is known.\n", "the host could not be reached: ssh said ssh: Could not resolve hostname mac.local: No such host is known.")]
    [InlineData("Connection to mac.local closed by remote host.\n", "'build' never reported how it finished, so it may not have run, or run only in part; the connection ended with exit 255: Connection to mac.local closed by remote host.")]
    [InlineData("", "'build' never reported how it finished, so it may not have run, or run only in part; the connection ended with exit 255")]
    public void NeverFinished_SaysTheHostWasNotReached_OnlyWhereSshNeverConnected(string standardError, string expected)
    {
        var said = HostProbes.NeverFinished("'build'", HostResults.Failed(255, standardError), new HostConnection { Host = HostId.Ssh("mac") });

        Assert.Equal(expected, said);
    }

    /// <summary>A connection to mac.invalid's addresses, learnt as <paramref name="addresses"/>; nothing here looks the name up again.</summary>
    private static ResolvedAddresses Learnt(params string[] addresses)
        => new(new AddressResolution("mac.invalid", Attempts: 1, addresses), Substitute.For<IHostAddressResolver>());
}
