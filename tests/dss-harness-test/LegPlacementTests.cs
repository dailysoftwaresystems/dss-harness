using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>Where a leg runs, given what each host was measured to be, and what the check reports.</summary>
public sealed class LegPlacementTests
{
    private static readonly HarnessConfig Config = new()
    {
        Hosts = new HostsConfig
        {
            Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" } },
            Ssh =
            {
                ["mac"] = new SshHostConfig { RepositoryPath = "/Users/dev/repo" },
                ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" },
            },
        },
    };

    private static readonly HostReport Windows = Measured(HostId.Local, "windows", "x86_64");

    private static readonly HostReport Ubuntu = Measured(HostId.Wsl("Ubuntu"), "linux", "x86_64");

    private static readonly HostReport Mac = Measured(HostId.Ssh("mac"), "macos", "arm64");

    private static readonly HostReport Pi = Measured(HostId.Ssh("pi"), "linux", "arm64");

    [Fact]
    public void Candidates_AreThisMachine_ThenWsl_ThenSsh_EachInDeclaredOrder()
    {
        Assert.Equal(
            ["local", "wsl Ubuntu", "ssh mac", "ssh pi"],
            LegPlacement.Candidates(Config, HostDoubles.Leg("linux", "arm64")).Select(host => host.ToString()));
    }

    [Fact]
    public void Candidates_LeaveOutTheWslDistributions_ForALegThatIsNotLinux()
    {
        // A distribution runs Linux only; measuring one for this leg would install DssHarness there for nothing.
        Assert.Equal(
            ["local", "ssh mac", "ssh pi"],
            LegPlacement.Candidates(Config, HostDoubles.Leg("macos", "arm64")).Select(host => host.ToString()));
    }

    [Fact]
    public void Candidates_AreOnlyTheHost_ALegNames()
    {
        var leg = new LegConfig { Os = "macos", Processor = "arm64", Config = "debug", Ssh = "mac" };

        Assert.Equal(["ssh mac"], LegPlacement.Candidates(Config, leg).Select(host => host.ToString()));
    }

    [Fact]
    public void Candidates_NameAPinnedHost_AsTheConfigurationDeclaresIt()
    {
        // ssh applies "Host mac" to "mac" and never to "MAC", so the leg's own spelling must not reach it.
        var leg = new LegConfig { Os = "macos", Processor = "arm64", Config = "debug", Ssh = "MAC" };

        Assert.Equal("mac", Assert.Single(LegPlacement.Candidates(Config, leg)).Name);
    }

    [Fact]
    public void Place_ChoosesTheFirstHost_ThatProvidesWhatTheLegNeeds()
    {
        var placement = Place(HostDoubles.Leg("linux", "arm64"), Windows, Ubuntu, Mac, Pi);

        Assert.True(placement.Runnable);
        Assert.Equal(HostId.Ssh("pi"), placement.Host?.Host);
    }

    [Fact]
    public void Place_PrefersThisMachine_WhenItCanRunTheLeg()
    {
        Assert.Equal(HostId.Local, Place(HostDoubles.Leg("windows", "x86_64"), Windows, Ubuntu).Host?.Host);
    }

    [Fact]
    public void Place_ChoosesThisMachine_OverAWslDistributionThatFitsAsWell()
    {
        var linux = Measured(HostId.Local, "linux", "x86_64");

        Assert.Equal(HostId.Local, Place(HostDoubles.Leg("linux", "x86_64"), linux, Ubuntu).Host?.Host);
    }

    [Fact]
    public void Place_ChoosesTheSshHostDeclaredFirst_WhenTwoFit()
    {
        // The order the measurements arrive in says nothing; the order the configuration declares does.
        var macRunningLinux = Measured(HostId.Ssh("mac"), "linux", "arm64");

        Assert.Equal(HostId.Ssh("mac"), Place(HostDoubles.Leg("linux", "arm64"), Windows, Ubuntu, Pi, macRunningLinux).Host?.Host);
    }

    [Fact]
    public void Place_ExplainsEveryCandidate_WhenNoneFits()
    {
        var placement = Place(HostDoubles.Leg("macos", "x86_64"), Windows, Ubuntu, Mac, Pi);

        Assert.False(placement.Runnable);
        Assert.Contains("local: it runs windows, and the leg needs macos", placement.Reason, StringComparison.Ordinal);
        Assert.Contains("ssh mac: it is arm64, and the leg runs natively on x86_64", placement.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Place_GivesAnUnavailableHost_ItsOwnReason()
    {
        var offline = new HostReport { Host = HostId.Ssh("pi"), Reason = "ssh could not connect: Connection timed out" };

        var placement = Place(HostDoubles.Leg("linux", "arm64"), Windows, Ubuntu, Mac, offline);

        Assert.Contains("ssh pi: ssh could not connect: Connection timed out", placement.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Place_RunsAnEmulatedLeg_OnlyWhereItsEmulatorsCheckPassed()
    {
        var leg = new LegConfig { Os = "linux", Processor = "arm64", Emulator = "qemu-arm64", Config = "debug" };
        var broken = Ubuntu with { Emulators = Checks(EmulatorCheck.Unavailable("qemu-aarch64 is missing")) };
        var working = Ubuntu with { Emulators = Checks(new EmulatorCheck(true, null, "aarch64")) };

        Assert.Contains(
            "wsl Ubuntu: emulator 'qemu-arm64' cannot run there: qemu-aarch64 is missing",
            Place(leg, Windows, broken).Reason,
            StringComparison.Ordinal);

        Assert.Equal(HostId.Wsl("Ubuntu"), Place(leg, Windows, working).Host?.Host);
    }

    /// <summary>
    /// A leg whose toolchain names a developer environment is turned away from a host that cannot set
    /// it up - as a tool missing where the host looked and has none, as unavailable where it could not
    /// look, and as a defect in this tool where it was never asked - and placed where it can. A copy
    /// starts nothing there, and needs none.
    /// </summary>
    [Fact]
    public void Place_TurnsALegAway_WhereItsDeveloperEnvironmentCannotBeSetUp()
    {
        var config = new HarnessConfig
        {
            DeveloperEnvironments = { ["vs"] = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio } },
            Toolchains = { ["msvc"] = new ToolchainConfig { Platforms = ["windows"], Env = { ["CC"] = "cl" }, DeveloperEnvironment = "vs" } },
        };
        var leg = new SelectedLeg("win", new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Toolchain = "msvc" });
        var without = Windows with
        {
            DeveloperEnvironments = new Dictionary<string, DeveloperEnvironmentCheck>(StringComparer.OrdinalIgnoreCase)
            {
                ["vs"] = DeveloperEnvironmentCheck.Nowhere("no Visual Studio instance there has the component 'X'"),
            },
        };
        var with = Windows with
        {
            DeveloperEnvironments = new Dictionary<string, DeveloperEnvironmentCheck>(StringComparer.OrdinalIgnoreCase)
            {
                ["vs"] = DeveloperEnvironmentCheck.Installed(@"C:\VS", "18.0.1"),
            },
        };

        HostReport Answering(DeveloperEnvironmentCheck check) => Windows with
        {
            DeveloperEnvironments = new Dictionary<string, DeveloperEnvironmentCheck>(StringComparer.OrdinalIgnoreCase) { ["vs"] = check },
        };

        LegPlacement Placed(LegWorkload workload, HostReport host)
            => LegPlacement.Place(config, leg, workload, new Dictionary<HostId, HostReport> { [host.Host] = host });

        var turnedAway = Placed(LegWorkload.BuildOnly, without);

        Assert.False(turnedAway.Runnable);
        Assert.Equal(LegVerdict.SkippedToolMissing, turnedAway.Verdict);
        Assert.Equal(
            $"{HostId.Local}: developer environment 'vs' cannot be set up there: no Visual Studio instance there has the component 'X'",
            turnedAway.Reason);

        var unknown = Placed(LegWorkload.BuildOnly, Answering(DeveloperEnvironmentCheck.Unreadable("vswhere did not answer")));

        Assert.Equal(LegVerdict.SkippedUnavailable, unknown.Verdict);
        Assert.Equal(
            $"{HostId.Local}: whether developer environment 'vs' can be set up there could not be established: vswhere did not answer",
            unknown.Reason);

        var unnamed = Placed(LegWorkload.BuildOnly, Answering(new DeveloperEnvironmentCheck(DeveloperEnvironmentFound.Installed, null, null, null)));

        Assert.Equal(LegVerdict.SkippedUnavailable, unnamed.Verdict);
        Assert.EndsWith("could not be established: the host named no instance", unnamed.Reason, StringComparison.Ordinal);

        var neverAsked = Placed(new LegWorkload(Build: false, Test: true, []), Windows);

        Assert.Equal(LegVerdict.Poisoned, neverAsked.Verdict);
        Assert.Equal(
            $"{HostId.Local}: whether developer environment 'vs' can be set up there was never asked, which is a defect in this tool: "
            + "a host is asked about every developer environment a leg starts in",
            neverAsked.Reason);

        Assert.True(Placed(LegWorkload.BuildOnly, with).Runnable);
        Assert.True(Placed(LegWorkload.Copy, without).Runnable);
    }

    [Fact]
    public void Place_PassesOverAHost_ThatWasNotMeasured()
    {
        Assert.Equal(HostId.Ssh("pi"), Place(HostDoubles.Leg("linux", "arm64"), Windows, Pi).Host?.Host);
    }

    /// <summary>
    /// A leg's os names its test invocation's section however it is spelled, as every per-system setting is read: a leg
    /// declaring "Windows" is asked for the runner its windows section declares, and not the one the shared section or
    /// another system's does.
    /// </summary>
    [Theory]
    [InlineData("Windows")]
    [InlineData("windows")]
    public void Place_RequiresTheTestRunnersOfTheSection_TheLegsOsNamesHoweverSpelled(string os)
    {
        var test = new TestConfig
        {
            All = new TestInvocation { Runner = "ctest" },
            Windows = new TestInvocation { Runner = "gtest" },
        };
        var leg = new SelectedLeg("win", new LegConfig { Os = os, Processor = "x86_64", Config = "debug", Test = test });
        var withGtest = Windows with
        {
            Programs = new Dictionary<string, ProgramLocation>(StringComparer.OrdinalIgnoreCase)
            {
                ["gtest"] = new ProgramLocation("gtest", ProgramFound.OnPath, "C:/bin/gtest.exe"),
            },
        };

        var placed = LegPlacement.Place(new HarnessConfig(), leg, LegWorkload.BuildAndTest, new Dictionary<HostId, HostReport> { [withGtest.Host] = withGtest });

        Assert.True(placed.Runnable);

        var without = Windows with
        {
            Programs = new Dictionary<string, ProgramLocation>(StringComparer.OrdinalIgnoreCase)
            {
                ["ctest"] = new ProgramLocation("ctest", ProgramFound.OnPath, "C:/bin/ctest.exe"),
            },
        };

        var refused = LegPlacement.Place(new HarnessConfig(), leg, LegWorkload.BuildAndTest, new Dictionary<HostId, HostReport> { [without.Host] = without });

        Assert.False(refused.Runnable);
        Assert.Contains("gtest", refused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("ctest", refused.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg that starts a declared tool no copy of which meets its minimum version is a tool missing, turned away
    /// before anything of it starts: its work would otherwise run under a version its configuration says it does not
    /// use, and fail, where it fails, for a reason naming something else. The refusal says what the host found and the
    /// command that installs one; a leg the tool is not declared for starts the copy the PATH names, as ever.
    /// </summary>
    [Fact]
    public void Place_TurnsALegAway_ThatStartsAToolBelowItsMinimumVersion_WhereTheToolIsDeclaredForIt()
    {
        const string Found = "no copy of 'python3' there is at least 3.12.0, the minVersion it is declared with: /usr/bin/python3 is 3.9.6";
        var test = new TestConfig { All = new TestInvocation { Runner = "python3" } };
        var leg = new SelectedLeg("win", new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Test = test });
        var host = Windows with
        {
            Programs = new Dictionary<string, ProgramLocation>(StringComparer.Ordinal)
            {
                ["python3"] = new ProgramLocation("python3", ProgramFound.BelowFloor, "/usr/bin/python3", Found),
            },
        };
        var reports = new Dictionary<HostId, HostReport> { [host.Host] = host };

        HarnessConfig Declaring(List<string>? platforms) => new()
        {
            Tools =
            [
                new ToolConfig
                {
                    Name = "python3",
                    Probe = new ToolProbe(),
                    MinVersion = "3.12.0",
                    Platforms = platforms,
                    Install = { ["windows"] = new ToolInstall { Manager = "winget", Id = "Python.Python.3.12" } },
                },
            ],
        };

        var refused = LegPlacement.Place(Declaring(null), leg, LegWorkload.BuildAndTest, reports);

        Assert.False(refused.Runnable);
        Assert.Equal(LegVerdict.SkippedToolMissing, refused.Verdict);
        Assert.Equal(
            $"local: {Found}; run 'dssharness install-missing-tools --legs win', which installs one as its tools entry declares, "
            + "or put a copy of at least that version where a leg there finds programs: on the PATH, or in a directory searched for programs",
            refused.Reason);

        Assert.True(LegPlacement.Place(Declaring(["linux"]), leg, LegWorkload.BuildAndTest, reports).Runnable);

        // A tool scoped to legs of another toolchain is held to its minimum for those, and this leg starts it as found.
        var scoped = new HarnessConfig
        {
            Toolchains = { ["msvc"] = new ToolchainConfig { Platforms = ["windows"] } },
            Tools = [new ToolConfig { Name = "python3", Probe = new ToolProbe(), MinVersion = "3.12.0", Toolchains = ["msvc"] }],
        };

        Assert.True(LegPlacement.Place(scoped, leg, LegWorkload.BuildAndTest, reports).Runnable);
    }

    /// <summary>
    /// A copy that meets the minimum behind an older one a directory raised for another tool holds turns the leg away as
    /// a tool missing, its reason saying what to update; one no copy of which could be shown to meet it is not known
    /// either way, unavailable; neither names an install, which would settle neither; and a tool that declares no
    /// install there is told only where a copy has to be.
    /// </summary>
    [Fact]
    public void Place_TurnsALegAway_ForAMinimumShadowedOrNotKnownToBeMet_AsItsReasonSays()
    {
        const string Below = "no copy of 'python3' there is at least 3.12.0, the minVersion it is declared with: /usr/bin/python3 is 3.9.6";
        var test = new TestConfig { All = new TestInvocation { Runner = "python3" } };
        var leg = new SelectedLeg("win", new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Test = test });

        LegPlacement Placed(ProgramFound found, string reason, bool install = true)
        {
            var host = Windows with
            {
                Programs = new Dictionary<string, ProgramLocation>(StringComparer.Ordinal)
                {
                    ["python3"] = new ProgramLocation("python3", found, "/usr/bin/python3", reason),
                },
            };
            var tool = new ToolConfig { Name = "python3", Probe = new ToolProbe(), MinVersion = "3.12.0" };

            if (install)
            {
                tool.Install["windows"] = new ToolInstall { Manager = "winget", Id = "Python.Python.3.12" };
            }

            return LegPlacement.Place(new HarnessConfig { Tools = [tool] }, leg, LegWorkload.BuildAndTest, new Dictionary<HostId, HostReport> { [host.Host] = host });
        }

        var shadowed = Placed(ProgramFound.FloorShadowed, "shadowed, so update it");
        var unknown = Placed(ProgramFound.FloorUnknown, "not known to meet it");
        var nowhereToInstall = Placed(ProgramFound.BelowFloor, Below, install: false);

        Assert.Equal((LegVerdict.SkippedToolMissing, "local: shadowed, so update it"), (shadowed.Verdict, shadowed.Reason));
        Assert.Equal((LegVerdict.SkippedUnavailable, "local: not known to meet it"), (unknown.Verdict, unknown.Reason));
        Assert.Equal(
            (LegVerdict.SkippedToolMissing,
                $"local: {Below}; put a copy of at least that version where a leg there finds programs: on the PATH, or in a directory searched for programs"),
            (nowhereToInstall.Verdict, nowhereToInstall.Reason));
    }

    /// <summary>
    /// A program an emulator is checked with, held to a declared tool's minimum version, leaves the emulator able to run
    /// there and turns away only a leg through it that the tool is declared for, as the leg's own programs are: below it,
    /// a tool missing naming the install; not known to meet it, unavailable. A leg the tool is not declared for runs
    /// through the copy there; and an emulator whose environment sets a PATH of its own holds no program from the survey.
    /// </summary>
    [Fact]
    public void Place_HoldsALegThroughAnEmulator_ToTheMinimumOfAToolItsEmulatorStarts_OnlyWhereTheToolIsDeclaredForIt()
    {
        const string Below = "no copy of 'qemu-aarch64' there is at least 8.0.0, the minVersion it is declared with: /usr/bin/qemu-aarch64 is 6.2.0";
        var leg = new SelectedLeg("arm", new LegConfig { Os = "linux", Processor = "arm64", Emulator = "qemu-arm64", Config = "debug" });

        LegPlacement Placed(ProgramFound found, List<string>? legs = null, Dictionary<string, string>? env = null, LegWorkload? workload = null)
        {
            var host = Ubuntu with
            {
                Emulators = Checks(new EmulatorCheck(true, null, "aarch64")),
                Programs = new Dictionary<string, ProgramLocation>(StringComparer.Ordinal)
                {
                    ["qemu-aarch64"] = new ProgramLocation("qemu-aarch64", found, "/usr/bin/qemu-aarch64", Below),
                },
            };
            var config = new HarnessConfig
            {
                Hosts = Config.Hosts,
                Emulators =
                {
                    ["qemu-arm64"] = new EmulatorConfig
                    {
                        HostOs = "linux",
                        HostProcessor = "x86_64",
                        Processor = "arm64",
                        Env = env ?? new(StringComparer.OrdinalIgnoreCase),
                        Witness = new EmulatorWitness { Command = ["qemu-aarch64", "./witness"], Pattern = "aarch64" },
                    },
                },
                Tools =
                [
                    new ToolConfig
                    {
                        Name = "qemu-aarch64",
                        Probe = new ToolProbe(),
                        MinVersion = "8.0.0",
                        Legs = legs,
                        Install = { ["linux"] = new ToolInstall { Manager = "apt", Id = "qemu-user" } },
                    },
                ],
            };

            return LegPlacement.Place(config, leg, workload ?? LegWorkload.BuildAndTest, new Dictionary<HostId, HostReport> { [host.Host] = host });
        }

        var below = Placed(ProgramFound.BelowFloor);
        var unknown = Placed(ProgramFound.FloorUnknown);

        Assert.Equal(LegVerdict.SkippedToolMissing, below.Verdict);
        Assert.Equal(
            $"wsl Ubuntu: {Below}; run 'dssharness install-missing-tools --legs arm', which installs one as its tools entry declares, "
            + "or put a copy of at least that version where a leg there finds programs: on the PATH, or in a directory searched for programs",
            below.Reason);
        Assert.Equal((LegVerdict.SkippedUnavailable, $"wsl Ubuntu: {Below}"), (unknown.Verdict, unknown.Reason));
        Assert.Equal(HostId.Wsl("Ubuntu"), Placed(ProgramFound.BelowFloor, legs: ["native"]).Host?.Host);
        Assert.Equal(HostId.Wsl("Ubuntu"), Placed(ProgramFound.BelowFloor, env: new(StringComparer.OrdinalIgnoreCase) { ["PATH"] = "/opt/qemu/bin" }).Host?.Host);
        Assert.Equal(HostId.Wsl("Ubuntu"), Placed(ProgramFound.BelowFloor, workload: LegWorkload.Copy).Host?.Host);
    }

    [Fact]
    public void TheCheck_FailsForANamedLegThatCannotRun_ButNotForADeclaredOne()
    {
        // A switched-off machine is normal; a leg asked for by name that cannot run is not.
        var runnable = new LegPlacement(Selected("a"), Windows, null);
        var stranded = new LegPlacement(Selected("b"), null, "no host");

        Assert.True(new LegsReport([runnable, stranded], [Windows], Named: false).Passed);
        Assert.False(new LegsReport([runnable, stranded], [Windows], Named: true).Passed);
        Assert.False(new LegsReport([stranded], [Windows], Named: false).Passed);
        Assert.False(new LegsReport([], [], Named: false).Passed);
    }

    [Fact]
    public void Render_ExitsWithTheLegsCode_AndWritesJsonThatParses()
    {
        var report = new LegsReport(
            [new LegPlacement(Selected("a"), Windows, null), new LegPlacement(Selected("b"), null, "no host")],
            [Windows],
            Named: true);

        var outcome = LegsReports.Render(report, json: true);

        Assert.Equal(LegsExit.Unavailable, outcome.ExitCode);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var legs = document.RootElement.GetProperty("legs");
        Assert.Equal(2, legs.GetArrayLength());
        Assert.Equal("local", legs[0].GetProperty("host").GetString());
        Assert.Equal("no host", legs[1].GetProperty("reason").GetString());
    }

    [Fact]
    public void Render_NamesTheHostAndTheEmulator_EachRunnableLegUses_AndWhatWasInstalled()
    {
        var emulated = new SelectedLeg("arm", new LegConfig { Os = "linux", Processor = "arm64", Emulator = "qemu-arm64", Config = "debug" });
        var host = Ubuntu with { Actions = ["installed DssHarness 1.2.0"] };

        var outcome = LegsReports.Render(new LegsReport([new LegPlacement(emulated, host, null)], [host], Named: false), json: false);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Contains("arm  runs on wsl Ubuntu (linux x86_64) through qemu-arm64", outcome.Details!);
        Assert.Contains("wsl Ubuntu: installed DssHarness 1.2.0", outcome.Details!);
    }

    /// <summary>
    /// With -v each host's room is a line of its own - or why it could not be measured - so a host that is
    /// nearly full shows before a run fills it; without it, nothing is said. As data it is always there.
    /// </summary>
    [Fact]
    public void Render_SaysEachHostsRoom_UnderVerbose_AndAlwaysAsData()
    {
        var vps = new HostReport { Host = HostId.Ssh("vps"), Os = "linux", Processor = "arm64", Space = new DiskSpace(33L << 30, 48L << 30, "/") };
        var unmeasured = Windows with { SpaceUnmeasured = "the drive is not ready" };
        var report = new LegsReport([new LegPlacement(Selected("a"), vps, null)], [unmeasured, vps], Named: false);

        var quiet = LegsReports.Render(report, json: false);
        var verbose = LegsReports.Render(report, json: false, verbose: true);

        Assert.DoesNotContain(quiet.Details!, line => line.Contains("free of", StringComparison.Ordinal));
        Assert.Contains("ssh vps: 33 GiB free of 48 GiB on '/'", verbose.Details!);
        Assert.Contains("local: the room there could not be measured: the drive is not ready", verbose.Details!);

        using var document = JsonDocument.Parse(Assert.Single(LegsReports.Render(report, json: true).Data));
        var hosts = document.RootElement.GetProperty("hosts");

        Assert.Equal(33L << 30, hosts[1].GetProperty("space").GetProperty("freeBytes").GetInt64());
        Assert.Equal("/", hosts[1].GetProperty("space").GetProperty("filesystem").GetString());
        Assert.Equal("the drive is not ready", hosts[0].GetProperty("spaceUnmeasured").GetString());
    }

    [Fact]
    public void Render_SaysWhyAMeasuredHostWasPassedOver()
    {
        // A leg placed further down its candidates would otherwise say nothing of the host it skipped.
        var offline = new HostReport { Host = HostId.Ssh("pi"), Reason = "ssh could not connect: Connection timed out" };

        var outcome = LegsReports.Render(
            new LegsReport([new LegPlacement(Selected("a"), Windows, null)], [Windows, offline], Named: false),
            json: false);

        Assert.Contains("ssh pi: cannot run legs: ssh could not connect: Connection timed out", outcome.Details!);
    }

    private static LegPlacement Place(LegConfig leg, params HostReport[] measured)
        => LegPlacement.Place(Config, new SelectedLeg("leg", leg), LegWorkload.BuildAndTest, measured.ToDictionary(report => report.Host));

    private static HostReport Measured(HostId host, string os, string processor) => new() { Host = host, Os = os, Processor = processor };

    private static Dictionary<string, EmulatorCheck> Checks(EmulatorCheck check)
        => new(StringComparer.OrdinalIgnoreCase) { ["qemu-arm64"] = check };

    private static SelectedLeg Selected(string name) => new(name, HostDoubles.Leg("windows", "x86_64"));
}
