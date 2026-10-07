using System.Globalization;
using System.Text.Json;
using NSubstitute;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// A consumer's host filled its disk with two builds at once, and nothing could free it: a build from clean
/// fills it again, deleting the worktree takes its local tree too. clean removes a leg's build directory
/// where the leg runs, writing nothing there first, and never from under a build of it.
/// </summary>
public sealed class CleanServiceTests
{
    private const string HostName = "pi";

    private const string HostTree = "/home/pi/repo";

    /// <summary>
    /// A leg on this machine has its build directory removed, and its line says what it held and the room
    /// left on its filesystem, in words and as data. Nothing is written first: no lock file, no records.
    /// </summary>
    [Fact]
    public async Task ALegsBuildDirectory_IsRemoved_SayingWhatItHeldAndTheRoomLeft_AndNothingIsWritten()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var directory = BuildDirectory(temp, harness, config);
        File.WriteAllText(Written(Path.Combine(directory, "obj", "deep", "a.o")), new string('a', 1000));

        var (outcome, leg) = await CleanAsync(temp, harness, config);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("passed", leg.GetProperty("verdict").GetString());
        Assert.StartsWith($"removed 1000 bytes from '{directory}'; ", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains(" free of ", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);

        var space = leg.GetProperty("space");
        Assert.Equal(directory, space.GetProperty("directory").GetString());
        Assert.Equal(1000, space.GetProperty("buildBytes").GetInt64());
        Assert.True(space.GetProperty("removed").GetBoolean());
        Assert.True(space.GetProperty("disk").GetProperty("totalBytes").GetInt64() > 0);

        Assert.False(Directory.Exists(directory));
        Assert.Equal([], Directory.GetDirectories(Path.GetDirectoryName(directory)!));
        Assert.False(File.Exists(new HarnessLayout(temp.Path, temp.Path).LockFile));
    }

    /// <summary>
    /// A host cleaning a leg for another machine names the build directory from its home, as <c>~</c>, in words
    /// and as data: the machine that asked puts both in its own output.
    /// </summary>
    [Fact]
    public async Task AHostAnsweringAnotherMachine_NamesTheBuildDirectoryFromItsHome()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory(home: HostDoubles.HomeAt(temp.Path));
        var config = OneLocalLeg(harness);
        var directory = BuildDirectory(temp, harness, config);
        File.WriteAllText(Written(Path.Combine(directory, "a.o")), "a");
        var shown = "~" + directory[temp.Path.Length..];

        var (outcome, leg) = await CleanAsync(temp, harness, config);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.StartsWith($"removed 1 byte from '{shown}'; ", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(shown, leg.GetProperty("space").GetProperty("directory").GetString());
    }

    /// <summary>
    /// A host cleaning for another machine that can place no leg says why from its home too: the ledger it ends
    /// with hands each leg's reason to the machine that asked, which puts it in its own output.
    /// </summary>
    [Fact]
    public async Task AHostAnsweringAnotherMachine_NamesItsHomeAsTilde_WhenNoLegCanBeCleaned()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory(home: HostDoubles.HomeAt(temp.Path));
        var gone = temp.Combine("gone");
        var inspector = new RecordingInspector(host => new HostReport { Host = host, Reason = $"'{gone}' could not be read" });

        var (outcome, leg) = await CleanAsync(temp, harness, OneLocalLeg(harness), inspector);

        Assert.NotEqual(HarnessExit.Success, outcome.ExitCode);
        Assert.EndsWith($"'{Path.Combine("~", "gone")}' could not be read", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(temp.Path, leg.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A dry run says what each build directory holds and the room beside it, and removes nothing.</summary>
    [Fact]
    public async Task ADryRun_SaysWhatTheBuildDirectoryHolds_AndRemovesNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var directory = BuildDirectory(temp, harness, config);
        var file = Written(Path.Combine(directory, "a.o"));
        File.WriteAllText(file, new string('a', 2048));

        var (outcome, leg) = await CleanAsync(temp, harness, config, dryRun: true);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.StartsWith($"2 KiB in '{directory}'; ", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(leg.GetProperty("space").GetProperty("removed").GetBoolean());
        Assert.Equal(2048, leg.GetProperty("space").GetProperty("buildBytes").GetInt64());
        Assert.True(File.Exists(file));
    }

    /// <summary>
    /// A leg a run is building is refused-locked, naming the run, and its build directory is left whole: a
    /// build's objects are not removed from under it.
    /// </summary>
    [Fact]
    public async Task ALegARunIsBuilding_IsRefusedLocked_AndItsBuildDirectoryIsLeftWhole()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var directory = BuildDirectory(temp, harness, config);
        var file = Written(Path.Combine(directory, "a.o"));
        File.WriteAllText(file, "object");
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);

        await using var held = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            PlacedLeg.BuildLock(
                HostId.Local,
                temp.Path,
                VariantKey.For(config, config.Legs.Single().Value, harness.Platform.PlatformKey),
                RunId.New(),
                "build"),
            TestContext.Current.CancellationToken);

        var (outcome, leg) = await CleanAsync(temp, harness, config, runLock: runLock);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal("refused-locked", leg.GetProperty("verdict").GetString());
        Assert.Contains(held.Entry.Holder.RunId, leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(file));
    }

    /// <summary>
    /// What an interrupted removal left aside is removed by the next clean of the leg, and counted with what
    /// it removes: nothing builds in a directory already moved out of a build's way.
    /// </summary>
    [Fact]
    public async Task WhatAnInterruptedRemovalLeftAside_IsRemovedByTheNextClean()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var directory = BuildDirectory(temp, harness, config);
        var aside = Path.Combine(Path.GetDirectoryName(directory)!, "." + Path.GetFileName(directory) + ".removing");
        File.WriteAllText(Written(Path.Combine(aside, "left.o")), new string('l', 100));
        File.WriteAllText(Written(Path.Combine(directory, "new.o")), new string('n', 20));

        var (_, leg) = await CleanAsync(temp, harness, config);

        Assert.Equal("passed", leg.GetProperty("verdict").GetString());
        Assert.Equal(120, leg.GetProperty("space").GetProperty("buildBytes").GetInt64());
        Assert.False(Directory.Exists(aside));
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>
    /// A build directory that is a link was put somewhere on purpose: it is left alone, and so is what it
    /// points at, and the leg says why.
    /// </summary>
    [Fact]
    public async Task ABuildDirectoryThatIsALink_IsLeftAlone_WithWhatItPointsAt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var directory = BuildDirectory(temp, harness, config);
        var elsewhere = temp.WriteFile(Path.Combine("elsewhere", "a.o"), "object");
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);

        TestLinks.OrSkip(() => Directory.CreateSymbolicLink(directory, Path.GetDirectoryName(elsewhere)!));

        var (_, leg) = await CleanAsync(temp, harness, config);

        Assert.Equal("failed", leg.GetProperty("verdict").GetString());
        Assert.Contains("is a link, so nothing was removed", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(elsewhere));
        Assert.True(Directory.Exists(directory));
    }

    /// <summary>
    /// A leg on a host is cleaned by the DssHarness there, in the tree's copy, and on itself - asked only once
    /// the copy is known to be there - and what that host measured is the leg's line.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALegOnAHost_IsCleanedByTheHost_InTheTreesCopy(bool dryRun)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        HostCommand? sent = null;

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            sent = command;
            ScriptedHostCommands.Answer(command, HostLedger());
            return HostResults.Finished(command, 0);
        });

        var (outcome, leg) = await CleanAsync(temp, harness, OneHostLeg(), OnTheHost(), hosts: hosts, copyThere: true, dryRun: dryRun);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("removed 8 GiB from '/home/pi/repo/build/arm64-none-debug'; 30 GiB free of 48 GiB on '/'", leg.GetProperty("detail").GetString());
        Assert.Equal(8L << 30, leg.GetProperty("space").GetProperty("buildBytes").GetInt64());
        Assert.Equal("/", leg.GetProperty("space").GetProperty("disk").GetProperty("filesystem").GetString());

        var request = JsonSerializer.Deserialize<HostAgentRequest>(sent!.StandardInput!, HostAgentProtocol.JsonOptions)!;

        Assert.Equal(HostTree, request.Directory);
        Assert.Equal(
            ["clean", "--legs", "arm", "--json", RemoteLegRunner.HereOption, $"ssh {HostName}", .. dryRun ? new[] { CleanService.DryRunOption } : []],
            request.Arguments);
    }

    /// <summary>
    /// A tree never synced to a host has no copy there, and nothing to remove: said as that, and the host is
    /// sent no command, which it would refuse as a copy it cannot run in.
    /// </summary>
    [Fact]
    public async Task ALegOnAHostWithNoCopyOfTheTree_HasNothingRemoved_AndTheHostIsSentNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var hosts = new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command));

        var (outcome, leg) = await CleanAsync(temp, harness, OneHostLeg(), OnTheHost(), hosts: hosts, copyThere: false);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal($"nothing to remove: ssh {HostName} holds no copy of this tree at '{HostTree}'", leg.GetProperty("detail").GetString());
        Assert.Empty(hosts.Calls);
    }

    /// <summary>
    /// A run of this machine building a leg on a host holds this machine's lock for it: the leg is
    /// refused-locked here, and neither the host nor its copy is asked anything.
    /// </summary>
    [Fact]
    public async Task ALegOnAHostARunOfThisMachineIsBuilding_IsRefusedLocked_BeforeTheHostIsAsked()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var hosts = new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command));
        var transports = Substitute.For<ISyncTransportFactory>();

        await using var held = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            PlacedLeg.BuildLock(HostId.Ssh(HostName), HostTree, VariantKey.For(OneHostLeg(), OneHostLeg().Legs["arm"], "linux"), RunId.New(), "test"),
            TestContext.Current.CancellationToken);

        var (_, leg) = await CleanAsync(temp, harness, OneHostLeg(), OnTheHost(), runLock, hosts, transports: transports);

        Assert.Equal("refused-locked", leg.GetProperty("verdict").GetString());
        Assert.Contains(held.Entry.Holder.RunId, leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(hosts.Calls);
        transports.DidNotReceiveWithAnyArgs().For(default!);
    }

    /// <summary>
    /// The mutation workers a leg's sweeps keep beside its tree are removed with its build directory, each named, and
    /// what they held counted; a claim a dead sweep left on one is released, and said, with it. Another variant's workers,
    /// and what an earlier removal of them left aside, are not this leg's to remove - its own aside is.
    /// </summary>
    [Fact]
    public async Task ALegsMutationWorkers_AreRemovedWithItsBuildDirectory()
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var variant = Variant(harness, config);
        var first = workers.Made(variant, 1, 1000);
        var second = workers.Made(variant, 2, 24);
        var others = workers.Made(variant with { Sanitizer = "asan" }, 1, 10);
        var aside = workers.Aside(variant, 3, 100);
        var othersAside = workers.Aside(variant with { Sanitizer = "asan" }, 2, 100);

        File.WriteAllText(
            first + MutationWorkers.ClaimSuffix,
            "{ \"machine\": \"" + Environment.MachineName + "\", \"processId\": " + (int.MaxValue - 1).ToString(CultureInfo.InvariantCulture)
            + ", \"processStamp\": \"gone\", \"runId\": \"20250101-120000-deadbeef\", \"takenUtc\": \"2025-01-01T12:00:00Z\" }");

        var (outcome, leg) = await CleanAsync(temp, harness, config);

        Assert.False(File.Exists(first + MutationWorkers.ClaimSuffix), "a dead sweep's claim goes with the worker it claimed");
        Assert.Contains("An earlier run was abandoned", harness.StandardError.ToString(), StringComparison.Ordinal);
        var detail = leg.GetProperty("detail").GetString()!;

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.EndsWith(
            $"; removed 2 mutation worker(s), {DiskSpace.Size(Workers.Holding(1000) + Workers.Holding(24) + 100)} with what an earlier removal of them left aside: '{first}', '{second}'",
            detail,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(second));
        Assert.False(Directory.Exists(Workers.AsideOf(first)), "a worker moved aside is removed there");
        Assert.False(Directory.Exists(Workers.AsideOf(second)), "a worker moved aside is removed there");
        Assert.False(Directory.Exists(aside));
        Assert.True(Directory.Exists(others));
        Assert.True(Directory.Exists(othersAside));
    }

    /// <summary>A dry run says what each worker holds, and what an earlier removal left aside, and removes none of it.</summary>
    [Fact]
    public async Task ADryRun_SaysWhatEachWorkerHolds_AndRemovesNothing()
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var variant = Variant(harness, config);
        var made = workers.Made(variant, 1, 2048);
        var somebodys = workers.Unmarked(variant, 2);
        var aside = workers.Aside(variant, 3, 100);

        var (outcome, leg) = await CleanAsync(temp, harness, config, dryRun: true);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.EndsWith(
            $"; 2 mutation worker(s): {DiskSpace.Size(Workers.Holding(2048))} in '{made}', {DiskSpace.Size(4)} in '{somebodys}', which a clean leaves: "
            + $"nothing there says the harness made it, so it is yours to remove, {DiskSpace.Size(100)} a removal that did not finish left aside",
            leg.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.True(Directory.Exists(made));
        Assert.True(Directory.Exists(aside));
    }

    /// <summary>
    /// A sweep of the leg holding its lock keeps every worker from a clean, which says so; a worker a live sweep still
    /// claims is kept and named, the others removed; a directory under a worker's name that no sync made is said and
    /// left. A clean that kept a worker for a sweep is refused-locked, as one that kept the build directory is.
    /// </summary>
    [Fact]
    public async Task WorkersASweepHolds_AreKept_AndOnesNobodyMadeAreLeft()
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var variant = Variant(harness, config);
        var free = workers.Made(variant, 1, 10);
        var claimed = workers.Made(variant, 2, 10);
        var somebodys = workers.Unmarked(variant, 3);
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var sweep = RunId.New();

        await using (await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            MutationWorkers.SweepLock(HostId.Local, temp.Path, variant, sweep, MutationService.CommandName),
            TestContext.Current.CancellationToken))
        {
            var (_, locked) = await CleanAsync(temp, harness, config, runLock: runLock);

            Assert.Equal("refused-locked", locked.GetProperty("verdict").GetString());
            Assert.Contains($"its mutation workers were left, as a sweep of the leg holds them: ", locked.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.Contains(sweep.Value, locked.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.True(Directory.Exists(free));
        }

        var claim = RunId.New();

        new DirectoryClaims(harness.FileSystem, harness.Output, harness.Identity, MutationWorkers.Claims(MutationService.CommandName)).Claim(claimed, claim, force: false);

        var (_, leg) = await CleanAsync(temp, harness, config, runLock: runLock);
        var detail = leg.GetProperty("detail").GetString()!;

        Assert.Equal("refused-locked", leg.GetProperty("verdict").GetString());
        Assert.Contains($"removed 1 mutation worker(s), {DiskSpace.Size(Workers.Holding(10))}: '{free}'", detail, StringComparison.Ordinal);
        Assert.Contains($"worker 2, '{claimed}', is claimed by a sweep still running: ", detail, StringComparison.Ordinal);
        Assert.Contains(claim.Value, detail, StringComparison.Ordinal);
        Assert.Contains($"'{somebodys}' is named as worker 3, and was left: nothing there says the harness made it, so it is yours to remove", detail, StringComparison.Ordinal);
        Assert.False(Directory.Exists(free));
        Assert.True(Directory.Exists(claimed));
        Assert.True(Directory.Exists(somebodys));
    }

    /// <summary>The variant the one leg of <paramref name="config"/> builds on this machine.</summary>
    private static VariantKey Variant(HarnessFactory harness, HarnessConfig config)
        => VariantKey.For(config, config.Legs.Single().Value, harness.Platform.PlatformKey);

    /// <summary>
    /// Mutation workers beside a test's tree - outside its directory, as a worker is beside its tree - removed once the
    /// test is done, with whatever a removal left aside.
    /// </summary>
    private sealed class Workers(TempDirectory temp) : IDisposable
    {
        /// <summary>The marker a sync leaves in a copy it made.</summary>
        private const string Marker = """{"CreatedUtc":"2026-10-07T12:00:00Z","CreatedBy":"builder","Adopted":false,"Completed":true}""";

        private readonly List<string> _made = [];

        /// <summary>What a worker made holding <paramref name="bytes"/> in its build comes to, its marker with it.</summary>
        public static long Holding(long bytes) => bytes + Marker.Length;

        /// <summary>Worker <paramref name="number"/> of <paramref name="variant"/>, as a sync makes one, its build holding <paramref name="bytes"/>.</summary>
        public string Made(VariantKey variant, int number, int bytes)
        {
            var worker = Track(MutationWorkers.PathOf(temp.Path, variant, number));

            Write(Path.Combine(worker, HarnessLayout.DirectoryName, HarnessLayout.SyncedCopyMarkerName), Marker);
            Write(Path.Combine(variant.DirectoryUnder(worker), "a.o"), new string('o', bytes));

            return worker;
        }

        /// <summary>A directory under worker <paramref name="number"/>'s name that no sync made, holding four bytes.</summary>
        public string Unmarked(VariantKey variant, int number)
        {
            var worker = Track(MutationWorkers.PathOf(temp.Path, variant, number));

            Write(Path.Combine(worker, "notes.txt"), "mine");

            return worker;
        }

        /// <summary>Where a removal moves <paramref name="worker"/> aside before it removes it.</summary>
        public static string AsideOf(string worker) => Path.Combine(Path.GetDirectoryName(worker)!, "." + Path.GetFileName(worker) + ".removing");

        /// <summary>What an earlier removal of worker <paramref name="number"/> left aside, holding <paramref name="bytes"/>.</summary>
        public string Aside(VariantKey variant, int number, int bytes)
        {
            var aside = Track(AsideOf(MutationWorkers.PathOf(temp.Path, variant, number)));

            Write(Path.Combine(aside, "left.o"), new string('l', bytes));

            return aside;
        }

        public void Dispose()
        {
            foreach (var path in _made)
            {
                SyncKit.DeleteIfPresent(path);
                SyncKit.DeleteIfPresent(AsideOf(path));
                File.Delete(path + MutationWorkers.ClaimSuffix);
            }
        }

        private string Track(string path)
        {
            _made.Add(path);
            return path;
        }

        private static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }

    private static HarnessConfig OneLocalLeg(HarnessFactory harness) => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Legs = { ["native"] = HostDoubles.Leg(harness.Platform.PlatformKey, harness.Platform.Processor) },
    };

    /// <summary>
    /// A host that never says how a leg's clean finished skips that leg, and every other leg still has its line:
    /// the first leg to fail once ended the command, and the legs after it were never said at all.
    /// </summary>
    [Fact]
    public async Task AHostThatNeverAnswers_SkipsEachOfItsLegs_AndEveryLegHasItsLine()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneHostLeg();
        config.BuildConfigs["release"] = new BuildConfiguration();
        config.Legs["arm-release"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "release", Ssh = HostName };

        // The connection ends before the agent says how the command finished.
        var hosts = new ScriptedHostCommands((_, _) => HostResults.Ok(string.Empty));

        var (outcome, legs) = await CleanEveryLegAsync(temp, harness, config, OnTheHost(), hosts: hosts);

        Assert.Equal(["arm", "arm-release"], legs.Select(leg => leg.GetProperty("leg").GetString()).Order(StringComparer.Ordinal));
        Assert.All(legs, leg => Assert.Equal("skipped-unavailable", leg.GetProperty("verdict").GetString()));
        Assert.NotEqual(HarnessExit.Success, outcome.ExitCode);
    }

    private static HarnessConfig OneHostLeg() => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Hosts = new HostsConfig { Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree } } },
        Legs = { ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = HostName } },
    };

    /// <summary>This machine as it is, and the ssh host as a Linux arm64 machine that answers.</summary>
    private static RecordingInspector OnTheHost() => new(host => new HostReport
    {
        Host = host,
        Os = "linux",
        Processor = "arm64",
        Session = new HostSession(new HostConnection { Host = host, Address = "192.0.2.10" }, ".dotnet/tools/dssharness"),
    });

    /// <summary>The build directory the one leg of <paramref name="config"/> has on this machine.</summary>
    private static string BuildDirectory(TempDirectory temp, HarnessFactory harness, HarnessConfig config)
        => VariantKey.For(config, config.Legs.Single().Value, harness.Platform.PlatformKey).DirectoryUnder(temp.Path);

    /// <summary><paramref name="path"/>, with the directory it goes in made.</summary>
    private static string Written(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>What the host answers for the leg: its ledger, with what it removed and the room left there.</summary>
    private static string HostLedger() => """
        {
          "exitCode": 0,
          "legs": [
            {
              "leg": "arm",
              "verdict": "passed",
              "detail": "removed 8 GiB from '/home/pi/repo/build/arm64-none-debug'; 30 GiB free of 48 GiB on '/'",
              "durationSeconds": 1.5,
              "commandSeconds": 0,
              "space": {
                "directory": "/home/pi/repo/build/arm64-none-debug",
                "buildBytes": 8589934592,
                "removed": true,
                "disk": { "freeBytes": 32212254720, "totalBytes": 51539607552, "filesystem": "/" }
              }
            }
          ]
        }
        """;

    /// <summary>Runs clean over <paramref name="config"/> with --json, and returns how it ended and its one leg's line.</summary>
    private static async Task<(CommandOutcome Outcome, JsonElement Leg)> CleanAsync(
        TempDirectory temp,
        HarnessFactory harness,
        HarnessConfig config,
        RecordingInspector? inspector = null,
        RunLock? runLock = null,
        ScriptedHostCommands? hosts = null,
        bool dryRun = false,
        bool copyThere = true,
        ISyncTransportFactory? transports = null)
    {
        var (outcome, legs) = await CleanEveryLegAsync(temp, harness, config, inspector, runLock, hosts, dryRun, copyThere, transports);

        return (outcome, Assert.Single(legs));
    }

    /// <summary>Cleans every leg of <paramref name="config"/>, and returns the outcome and each leg's line.</summary>
    private static async Task<(CommandOutcome Outcome, IReadOnlyList<JsonElement> Legs)> CleanEveryLegAsync(
        TempDirectory temp,
        HarnessFactory harness,
        HarnessConfig config,
        RecordingInspector? inspector = null,
        RunLock? runLock = null,
        ScriptedHostCommands? hosts = null,
        bool dryRun = false,
        bool copyThere = true,
        ISyncTransportFactory? transports = null)
    {
        var loader = HostDoubles.Loader(config, temp.Path);

        if (transports is null)
        {
            var transport = Substitute.For<ISyncTransport>();
            transport.RootExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(copyThere);
            transports = Substitute.For<ISyncTransportFactory>();
            transports.For(Arg.Any<HostReport>()).Returns(transport);
        }

        var service = new CleanService(
            loader,
            new LegsService(loader, inspector ?? new RecordingInspector(host => new HostReport
            {
                Host = host,
                Os = harness.Platform.PlatformKey,
                Processor = harness.Platform.Processor,
            }), harness.Platform, harness.Output),
            runLock ?? new RunLock(harness.FileSystem, harness.Output, harness.Identity),
            transports,
            new RemoteLegRunner(hosts ?? new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command)), harness.Output),
            new LegExecutor(harness.Platform, harness.Output),
            SyncKit.Service(harness, loader),
            SyncKit.Transport(harness),
            harness.Identity,
            harness.FileSystem,
            harness.Platform,
            harness.Output);

        var outcome = await service.RunAsync(new CleanRequest(temp.Path, null, dryRun, Json: true), TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));

        return (outcome, [.. document.RootElement.GetProperty("legs").EnumerateArray().Select(leg => leg.Clone())]);
    }
}
