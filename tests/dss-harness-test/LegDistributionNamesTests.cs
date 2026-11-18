using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Runs;
using DssHarness.Core.Secrets;

namespace DssHarness.Tests;

/// <summary>
/// The names of a WSL distribution a run line may write: <c>{distribution}</c>, the one a WSL leg's own item names, and
/// <c>{distribution.&lt;item&gt;}</c>, the one a declared WSL item names, on any leg - filled in from the items as read
/// where the leg was placed, and refused, saying which case it is, wherever nothing fills them in.
/// </summary>
public sealed class LegDistributionNamesTests
{
    /// <summary>
    /// <c>{distribution}</c> is the distribution the leg's own WSL item names, and <c>{distribution.&lt;item&gt;}</c> the one
    /// that item names, matched ignoring case as an item is - on a WSL leg, and on any other, alike - never the item's name.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AWslItemsDistribution_IsFilledInWithWhatItsItemNames_NeverTheItemsName(bool inWsl)
    {
        var paths = Paths(inWsl ? Wsl("ubuntu-24", "Example-Linux") : Ssh());

        Assert.Equal(
            "wsl.exe -d Example-Linux -- true",
            LegPathNames.Expand("wsl.exe -d {distribution.Ubuntu-24} -- true", paths, "'probe' run line"));

        if (inWsl)
        {
            Assert.Equal("Example-Linux", LegPathNames.Expand("{distribution}", paths, "'probe' run line"));
        }
    }

    /// <summary>
    /// A distribution nothing fills in is refused, saying which case it is - a leg in no WSL distribution, an item the
    /// configuration does not declare, an item whose .env could not be read where the leg was placed, a run that reaches no
    /// leg - by the expansion and by the check made over a whole action before its first step, alike, under either policy:
    /// passed through, it would reach wsl.exe as its own text.
    /// </summary>
    [Theory]
    [InlineData("{distribution}", "ssh", HarnessExit.ConfigInvalid, "'probe' run line names '{distribution}', and leg 'arm' runs on ssh pi, which is no WSL distribution, so it has no distribution to put there. Name a WSL item's as '{distribution.<item>}', or take the name out.")]
    [InlineData("{distribution.Debian}", "ssh", HarnessExit.ConfigInvalid, "'probe' run line names '{distribution.Debian}', and no WSL item 'Debian' is declared under wslDistros: the declared ones are 'Ubuntu-24'.")]
    [InlineData("{distribution.Ubuntu-24}", "unread", HarnessExit.ConfigInvalid, "'probe' run line names '{distribution.Ubuntu-24}', and WSL item 'Ubuntu-24''s .env could not be read where leg 'arm' was placed, so its distribution is not known there; 'dssharness legs' there says why.")]
    [InlineData("{distribution}", "unread wsl", HarnessExit.ConfigInvalid, "'probe' run line names '{distribution}', and the .env of leg 'arm''s WSL item could not be read where it was placed, so its distribution is not known there; 'dssharness legs' there says why.")]
    [InlineData("{distribution}", "no leg", HarnessExit.UsageError, "'probe' run line names '{distribution}', and this run reaches no leg, so there is nothing to put there. Run it for a leg, or take the name out.")]
    [InlineData("{distribution.Ubuntu-24}", "no leg", HarnessExit.UsageError, "'probe' run line names '{distribution.Ubuntu-24}', and this run reaches no leg, so no WSL item was read for it. Run it for a leg, or take the name out.")]
    public void ADistributionNothingFillsIn_IsRefused_SayingWhichCaseItIs(string line, string @case, int exitCode, string said)
    {
        var identity = @case switch
        {
            "ssh" => Ssh(),
            "unread" => Ssh() with { Distributions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Ubuntu-24"] = null } },
            "unread wsl" => Wsl("Ubuntu-24", distribution: null),
            _ => null,
        };
        var paths = new LegPaths("/tree", identity is null ? null : "/tree/build/v") { Identity = identity };

        foreach (var policy in new[] { PlaceholderPolicy.Refuse, PlaceholderPolicy.LeaveAsWritten })
        {
            var refusal = Assert.Throws<HarnessException>(() => LegPathNames.Expand($"wsl.exe -d {line}", paths, "'probe' run line", policy));

            Assert.Equal(exitCode, refusal.ExitCode);
            Assert.Equal(said, refusal.Message);
        }

        if (identity is not null)
        {
            Assert.Equal(said, Assert.Throws<HarnessException>(() => LegPathNames.RefuseAbsentDistributions($"wsl.exe -d {line}", "'probe' run line", identity)).Message);
        }
    }

    /// <summary>
    /// Where the configuration is read and no leg is placed, a WSL item's distribution is refused for an item wslDistros
    /// does not declare, naming those it does; any declared item's stands, as <c>{distribution}</c> does, and a caller that
    /// does not know the items leaves the item to the expansion.
    /// </summary>
    [Fact]
    public void WhereTheConfigurationIsRead_AnUndeclaredItemsDistribution_IsRefused_NamingTheDeclaredOnes()
    {
        var refused = Assert.Throws<HarnessException>(() => LegPathNames.RefuseUnknown("{distribution.Debian}", "test.args", wslItems: ["Ubuntu-24", "Alpine"]));

        Assert.Equal(HarnessExit.ConfigInvalid, refused.ExitCode);
        Assert.Equal("test.args names '{distribution.Debian}', and no WSL item 'Debian' is declared under wslDistros: the declared ones are 'Alpine', 'Ubuntu-24'.", refused.Message);
        Assert.Equal(
            "test.args names '{distribution.Debian}', and no WSL item 'Debian' is declared under wslDistros: none is.",
            Assert.Throws<HarnessException>(() => LegPathNames.RefuseUnknown("{distribution.Debian}", "test.args", wslItems: [])).Message);

        LegPathNames.RefuseUnknown("{distribution.ubuntu-24} {distribution}", "test.args", wslItems: ["Ubuntu-24"]);
        LegPathNames.RefuseUnknown("{distribution.Debian}", "test.args");
    }

    /// <summary>
    /// The names are this tool's own: <c>{distribution}</c> among the rest, and every WSL item's by its family - so an
    /// input or a runner value of either name is refused where it is declared - and a name that names no item is none.
    /// </summary>
    [Fact]
    public void TheNames_AreThisToolsOwn()
    {
        Assert.Contains(LegPathNames.Distribution, LegPathNames.All);
        Assert.True(LegPathNames.IsOwned("distribution"));
        Assert.True(LegPathNames.IsOwned("distribution.Ubuntu"));
        Assert.False(LegPathNames.IsOwned("distribution."));
        Assert.False(LegPathNames.IsOwned("distributions"));
        Assert.Equal("Ubuntu", LegPathNames.ItemOf("distribution.Ubuntu"));
        Assert.Null(LegPathNames.ItemOf("distribution"));
    }

    /// <summary>
    /// A leg placed here is who it is with the distributions read here: its own item's where it runs in a WSL distribution,
    /// none where it does not, and every declared item's either way.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APlacedLegsIdentity_CarriesTheDistributionsReadWhereItWasPlaced(bool inWsl)
    {
        var told = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Ubuntu-24"] = "Example-Linux", ["Alpine"] = null };
        var leg = Placed(inWsl ? HostId.Wsl("ubuntu-24") : HostId.Ssh("pi")) with { Distributions = told };

        var identity = leg.IdentityFor("r1");

        Assert.Equal(inWsl, identity.InWsl);
        Assert.Equal(inWsl ? "Example-Linux" : null, identity.Distribution);
        Assert.Same(told, identity.Distributions);
    }

    /// <summary>
    /// A leg sent to a host carries the distributions read here - a host's copy holds no item to read them from - and none
    /// it has nothing for, which would fill a run line there with nothing; the host's command is told them, and a request
    /// telling a blank one is refused.
    /// </summary>
    [Fact]
    public async Task ALegSentToAHost_CarriesTheDistributionsReadHere_AndTheHostIsToldThem()
    {
        HostCommand? sent = null;
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            sent = command;
            ScriptedHostCommands.Answer(command, """{ "verdict": "passed", "exitCode": 0, "passed": true, "legs": [ { "leg": "wsl-debug", "verdict": "passed", "failure": false, "durationSeconds": 1, "detail": "1 test" } ] }""");

            return HostResults.Finished(command, 0);
        });
        var leg = Placed(HostId.Wsl("Example-Linux")) with
        {
            Distributions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Ubuntu-24"] = "Example-Linux", ["Alpine"] = null },
        };

        await new RemoteLegRunner(hosts, new HarnessFactory().Output).RunAsync("test", leg, [], cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(sent);
        var request = System.Text.Json.JsonSerializer.Deserialize<HostAgentRequest>(sent.StandardInput.Read(), HostAgentProtocol.JsonOptions)!;
        var told = Dispatch.Of(request);

        Assert.Equal(new Dictionary<string, string> { ["Ubuntu-24"] = "Example-Linux" }, request.Distributions);
        Assert.Equal("Example-Linux", HostItems.Told(told.Distributions).Of(HostId.Wsl("ubuntu-24")).Distribution);
        Assert.Null(HostItems.Told(told.Distributions).Of(HostId.Ssh("Ubuntu-24")).Distribution);
        Assert.Equal(
            "the request tells a blank distribution, or one of a blank item: 'Alpine'",
            Assert.Throws<HarnessException>(() => Dispatch.Of(new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Run,
                Distributions = new(StringComparer.OrdinalIgnoreCase) { ["Alpine"] = " " },
            })).Message);
    }

    private static LegPaths Paths(LegIdentity identity) => new("/tree", "/tree/build/v") { Identity = identity };

    /// <summary>A leg placed on <paramref name="host"/>.</summary>
    private static PlacedLeg Placed(HostId host)
        => new(
            "wsl-debug",
            new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug" },
            new HostReport
            {
                Host = host,
                Os = "linux",
                Processor = "x86_64",
                Session = new HostSession(
                    new HostConnection { Host = host, Distribution = host.Kind == HostKind.Wsl ? "Example-Linux" : null },
                    ".dotnet/tools/dssharness"),
            },
            Project: null,
            new VariantKey("x86_64", "gcc", "debug", null),
            TreeRoot: "/src/repo",
            HostTreeRoot: "/home/dev/repo",
            "/home/dev/repo/build/x86_64-gcc-debug",
            new WslHostConfig { RepositoryPath = "/home/dev/repo" },
            Emulated: false);

    /// <summary>A leg in WSL item <paramref name="item"/>, which names <paramref name="distribution"/>, the one WSL item declared.</summary>
    private static LegIdentity Wsl(string item, string? distribution)
        => new("arm", "linux", "x86_64", "gcc", "debug", "x86_64-gcc-debug", $"wsl {item}", "r1")
        {
            InWsl = true,
            Distribution = distribution,
            Distributions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [item] = distribution },
        };

    /// <summary>A leg on an ssh host, beside WSL item Ubuntu-24, which names Example-Linux.</summary>
    private static LegIdentity Ssh()
        => new("arm", "linux", "arm64", "gcc", "debug", "arm64-gcc-debug", "ssh pi", "r1")
        {
            Distributions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Ubuntu-24"] = "Example-Linux" },
        };
}
