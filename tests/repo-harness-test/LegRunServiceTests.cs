using System.Globalization;
using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// What becomes of a leg whose host's copy cannot be brought up to date before it runs, and where a
/// leg goes. Each copy problem is about one tree at one moment, so the legs on that tree say so under
/// their own verdict and every other leg still reports. A tree another run held ended the whole run,
/// and a transport that would not start failed every leg on the tree, as though its code had.
/// </summary>
public sealed class LegRunServiceTests
{
    private const string HostName = "pi";

    private const string HostTree = "/home/pi/repo";

    /// <summary>
    /// A tree another run is building on cannot be replaced, so the legs that need it are
    /// refused-locked, naming the run in the way, and nothing is carried there. Their own variant's
    /// lock is free - another run holds another variant - so it is the refused sync alone that keeps
    /// them off a copy this run never brought up to date.
    /// </summary>
    [Fact]
    public async Task ATreeAnotherRunIsUsing_RefusesItsLegs_AndTheOtherLegsStillReport()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = Substitute.For<ISyncService>();
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);

        await using var held = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            new LockRequest
            {
                Host = HostId.Ssh(HostName).ToString(),
                Tree = HostTree,
                Variant = "arm64-other-release",
                Scope = LockScope.TreeShared,
                RunId = RunId.New(),
                Command = "build",
            },
            TestContext.Current.CancellationToken);

        var verdicts = await RunAsync(temp, harness, TwoLegs(harness), SshAndLocal(harness), new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy }, runLock, sync);

        Assert.Equal("passed", verdicts["native"].Verdict);
        Assert.Equal("refused-locked", verdicts["arm"].Verdict);
        Assert.Contains(held.Entry.Holder.RunId, verdicts["arm"].Detail, StringComparison.Ordinal);
        await sync.DidNotReceiveWithAnyArgs().SyncAsync(default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// ssh or wsl.exe that would not start on this machine never reached the host - which the runner
    /// that starts it says, as that host being unavailable: the legs on that tree are unavailable
    /// there, saying why, and no verdict is claimed about code that never ran.
    /// </summary>
    [Fact]
    public async Task ATransportThatWouldNotStart_LeavesItsLegsUnavailable_AndTheOtherLegsStillReport()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = Substitute.For<ISyncService>();

        // The copy starts ssh through the runner every host command goes through, so what reaches the
        // run is what that runner makes of ssh not starting - never a refusal written for the test.
        var processes = Substitute.For<IProcessRunner>();
        processes.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProgramStartException("ssh", "'ssh' could not be started: No such file or directory"));
        var hosts = new HostCommandRunner(processes);
        var pi = new HostConnection
        {
            Host = HostId.Ssh(HostName),
            Address = "192.0.2.10",
            User = "harness",
            Port = 22,
            KeyFile = temp.Combine(".harness-config", "sshItems", HostName, ".key"),
            KnownHostsFile = temp.Combine(".harness-config", "sshItems", HostName, "known_hosts"),
            ConnectTimeoutSeconds = 10,
            KeepAliveSeconds = 15,
            LocalDirectory = temp.Path,
        };

        var unreached = await Assert.ThrowsAnyAsync<Exception>(
            () => hosts.RunAsync(pi, new HostCommand { Program = "true" }, TestContext.Current.CancellationToken));

        sync.SyncAsync(default!, default!, default!, default!, TestContext.Current.CancellationToken)
            .ThrowsAsyncForAnyArgs(unreached);

        var verdicts = await RunAsync(temp, harness, TwoLegs(harness), SshAndLocal(harness), new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy }, sync: sync);

        Assert.Equal("passed", verdicts["native"].Verdict);
        Assert.Equal("skipped-unavailable", verdicts["arm"].Verdict);
        Assert.Equal($"ssh {HostName} could not be reached: 'ssh' could not be started: No such file or directory", verdicts["arm"].Detail);
    }

    /// <summary>
    /// A program of this machine's own that will not start while a tree is read for its hosts' copies - git, in the
    /// middle of an upgrade - is no host being unavailable: the legs on that tree have begun, so they fail, naming the
    /// program, as any leg whose program will not start once it is running does, and nothing is carried for them.
    /// </summary>
    [Fact]
    public async Task AProgramOfThisMachinesThatWouldNotStartWhileItsTreeWasRead_FailsItsLegs()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = Substitute.For<ISyncService>();

        sync.ReadSourceAsync(default!, TestContext.Current.CancellationToken)
            .ThrowsAsyncForAnyArgs(new ProgramStartException("/usr/bin/git", "'/usr/bin/git' could not be started: Text file busy"));

        var verdicts = await RunAsync(temp, harness, TwoLegs(harness), SshAndLocal(harness), new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy }, sync: sync);

        Assert.Equal("passed", verdicts["native"].Verdict);
        Assert.Equal("failed", verdicts["arm"].Verdict);
        Assert.Contains("'/usr/bin/git' could not be started", verdicts["arm"].Detail, StringComparison.Ordinal);
        await sync.DidNotReceiveWithAnyArgs().SyncAsync(default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A tree that could not be read for its hosts' copies as the run began gives the legs on it their verdict, and only
    /// them: the leg on this machine still runs, and the run still names where its records are. Read ahead of the
    /// legs, outside them, its failure ended the whole run, with no ledger and no records named.
    /// </summary>
    [Fact]
    public async Task ATreeThatCouldNotBeRead_GivesItsOwnLegsTheirVerdict_AndTheOtherLegsStillRun()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = Substitute.For<ISyncService>();
        var ran = new List<string>();

        sync.ReadSourceAsync(default!, TestContext.Current.CancellationToken)
            .ThrowsAsyncForAnyArgs(new HarnessException(HarnessExit.CommandFailed, "git could not list what the tree ignores"));

        var outcome = await OutcomeAsync(
            temp,
            harness,
            TwoLegs(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy },
            sync: sync,
            ran: leg => ran.Add(leg.Name));

        var legs = Verdicts(outcome);

        Assert.Equal("passed", legs["native"].Verdict);
        Assert.Equal("failed", legs["arm"].Verdict);
        Assert.Equal("git could not list what the tree ignores", legs["arm"].Detail);
        Assert.Equal(["native"], ran);
        Assert.NotEmpty(RunDirectoryOf(outcome, json: true));
    }

    /// <summary>
    /// A run on what is staged reads, on the machine that would have synced each copy, whether a sync or a takeover began
    /// there and did not finish: where one did, that copy holds part of one tree and part of another, so its legs are
    /// inputs-moved, naming it and what to do, and its host is never asked to test it; the legs elsewhere run. A host asked
    /// to run on what it holds was never told by the dispatcher that what it holds is staged, so it could not refuse.
    /// </summary>
    [Theory]
    [InlineData(CopyMark.Unfinished, "is a copy whose sync began and did not finish", "Run without --use-staged, which syncs it first.")]
    [InlineData(CopyMark.AdoptionStopped, "was being taken over and the run stopped before it finished", "sync --adopt \"ssh pi\"', and run again.")]
    [InlineData(CopyMark.Complete, null, null)]
    [InlineData(CopyMark.None, null, null)]
    public async Task ARunOnWhatIsStaged_MakesInputsMovedTheLegsOfACopyLeftPartMade(CopyMark mark, string? partMade, string? remedy)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var asked = 0;

        var copy = Substitute.For<ISyncTransport>();
        copy.ReadMarkAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(mark);

        var transports = Substitute.For<ISyncTransportFactory>();
        transports.For(Arg.Any<HostReport>()).Returns(copy);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Interlocked.Increment(ref asked);
            ScriptedHostCommands.Answer(command, """{"legs": [{"leg": "arm", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });

        var outcome = await OutcomeAsync(
            temp,
            harness,
            TwoLegs(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true, UseStaged: true) { Workload = LegWorkload.Copy },
            hosts: hosts,
            transports: transports);

        var legs = Verdicts(outcome);

        Assert.Equal("passed", legs["native"].Verdict);
        await copy.Received(1).ReadMarkAsync(HostTree, Arg.Any<CancellationToken>());

        if (partMade is null)
        {
            Assert.Equal("passed", legs["arm"].Verdict);
            Assert.Equal(1, asked);
        }
        else
        {
            Assert.Equal("inputs-moved", legs["arm"].Verdict);
            Assert.StartsWith($"ssh {HostName}: '{HostTree}' {partMade}", legs["arm"].Detail, StringComparison.Ordinal);
            Assert.Contains("--use-staged has nothing current to run there", legs["arm"].Detail, StringComparison.Ordinal);
            Assert.EndsWith(remedy!, legs["arm"].Detail, StringComparison.Ordinal);
            Assert.Equal(LegExit.InputsMoved, outcome.ExitCode);
            Assert.Equal(0, asked);
        }
    }

    /// <summary>
    /// A run on what each host already holds reads nothing for carrying and carries nothing: --use-staged says the copies
    /// are current, and its legs test them as they are.
    /// </summary>
    [Fact]
    public async Task ARunOnWhatIsStaged_ReadsNothingForCarrying_AndCarriesNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = Substitute.For<ISyncService>();

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            ScriptedHostCommands.Answer(command, """{"legs": [{"leg": "arm", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });

        var outcome = await OutcomeAsync(
            temp,
            harness,
            TwoLegs(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true, UseStaged: true) { Workload = LegWorkload.Copy },
            sync: sync,
            hosts: hosts);

        var legs = Verdicts(outcome);

        Assert.Equal("passed", legs["native"].Verdict);
        Assert.Equal("passed", legs["arm"].Verdict);
        await sync.DidNotReceiveWithAnyArgs().ReadSourceAsync(default!, TestContext.Current.CancellationToken);
        await sync.DidNotReceiveWithAnyArgs().SyncAsync(default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A lock file nobody can use is no lock another run holds: it stops every run on every tree
    /// alike, so it ends this one as the refusal it is, rather than turning each leg away as locked
    /// and sending the reader to wait for a run that does not exist.
    /// </summary>
    [Fact]
    public async Task ALockFileThatCannotBeRead_EndsTheRun_RatherThanLockingEachLeg()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        temp.WriteFile(Path.Combine(".harness-config", "lock.json"), "not a lock file");

        var outcome = await OutcomeAsync(
            temp,
            harness,
            TwoLegs(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy });

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Contains("lock.json", outcome.Message, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));

        Assert.DoesNotContain(
            document.RootElement.GetProperty("legs").EnumerateArray(),
            leg => leg.GetProperty("verdict").GetString() == "refused-locked");
    }

    /// <summary>
    /// A host running a leg another machine dispatched to it runs it with the settings that machine's
    /// configuration gives it - its cores and its environment - and under its name wherever a reader
    /// sees one. Read as 'local', it ran with those of the machine that dispatched it.
    /// </summary>
    [Fact]
    public async Task ALegDispatchedHere_RunsWithTheSettingsOfTheHostItWasSentTo()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        var config = new HarnessConfig
        {
            Toolchains = { ["gcc"] = new ToolchainConfig { Platforms = [platform.PlatformKey] } },
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects = { new ProjectConfig { Name = "app", Type = "cmake", Path = "." } },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig { BuildCores = 7, Env = { ["RH_HOST"] = "local" } },
                Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree, BuildCores = 3, Env = { ["RH_HOST"] = "pi" } } },
            },
            Legs =
            {
                ["arm"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "gcc", Ssh = HostName },
            },
        };

        PlacedLeg? ran = null;

        var verdicts = await RunAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true, Here: HostId.Ssh(HostName)) { Workload = LegWorkload.Copy },
            ran: leg => ran = leg);

        Assert.Equal("passed", verdicts["arm"].Verdict);
        Assert.NotNull(ran);

        // Run here, on this machine, and locked and scheduled as it - but read, and named, as the
        // host the machine that dispatched it knows.
        Assert.Equal(HostId.Local, ran.Host.Host);
        Assert.Equal(HostId.Ssh(HostName), ran.Named);
        Assert.Equal(3, ran.HostSettings.BuildCores);
        Assert.Equal($"ssh {HostName}", ran.IdentityFor("run").Host);
        Assert.Equal($"ssh {HostName}", ran.ToPlan().Host);

        var build = ran.BuildRequestFor(config, temp.Path);

        Assert.Equal(3, build.Cores);
        Assert.Equal("pi", build.HostEnvironment["RH_HOST"]);
    }

    /// <summary>
    /// A refusal is read by whoever typed the command, so it names the host as they know it: on a
    /// host running legs another machine dispatched to it, two legs sharing a build directory share
    /// it on that host, never on 'local' - which, to that reader, is their own machine.
    /// </summary>
    [Fact]
    public async Task ARefusalOnAHostSentLegs_NamesItAsTheMachineThatSentThemKnowsIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig { Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree } } },
            Legs =
            {
                ["one"] = HostDoubles.Leg(platform.PlatformKey, platform.Processor),
                ["two"] = HostDoubles.Leg(platform.PlatformKey, platform.Processor),
            },
        };

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true, Here: HostId.Ssh(HostName)) { Workload = LegWorkload.Copy }));

        Assert.Contains($"on ssh {HostName}", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg's own work is held awake by the command its host declares, for exactly as long as the
    /// work runs - and on a host running a leg another machine dispatched to it, by that host's own
    /// command, never by the one 'local' declares for the machine that dispatched it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALegsOwnWork_IsHeldAwake_ByItsHostsCommand_UntilItEnds(bool sentHere)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var held = new HeldProcesses();

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig { KeepAwake = ["local-awake", "-w", "{pid}"] },
                Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree, KeepAwake = ["pi-awake", "-w", "{pid}"] } },
            },
            Legs = { ["native"] = HostDoubles.Leg(platform.PlatformKey, platform.Processor) },
        };

        bool? stoppedDuringTheWork = null;

        var verdicts = await RunAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true, Here: sentHere ? HostId.Ssh(HostName) : null) { Workload = LegWorkload.Copy },
            ran: _ => stoppedDuringTheWork = Assert.Single(held.Started).Stopping.IsCancellationRequested,
            keepAwake: held);

        Assert.Equal("passed", verdicts["native"].Verdict);
        Assert.False(stoppedDuringTheWork);

        var (request, stopping) = Assert.Single(held.Started);

        Assert.Equal(sentHere ? "pi-awake" : "local-awake", request.FileName);
        Assert.True(stopping.IsCancellationRequested);
    }

    /// <summary>
    /// A leg goes where a sync puts its tree whether or not the run syncs, and a program that host
    /// lacks turns it away there. Moved by what each command starts, a run on what a build had staged
    /// went to a host that had the program and never had the tree.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALegIsTurnedAwayWhereItsTreeGoes_NeverMovedToAHostThatHasTheProgram(bool useStaged)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig { Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree } } },
            Legs = { ["here"] = HostDoubles.Leg(platform.PlatformKey, platform.Processor) },
        };

        // This machine lacks what the command starts; the ssh host, which is the same kind of
        // machine, has it.
        var inspector = new RecordingInspector(host => new HostReport
        {
            Host = host,
            Os = platform.PlatformKey,
            Processor = platform.Processor,
            Programs = new Dictionary<string, ProgramLocation>(StringComparer.Ordinal)
            {
                ["rh-probe"] = new("rh-probe", host.Kind == HostKind.Local ? ProgramFound.Nowhere : ProgramFound.OnPath, "/usr/bin/rh-probe"),
            },
            Session = host.Kind == HostKind.Local ? null : Session(host),
        });

        var verdicts = await RunAsync(
            temp,
            harness,
            config,
            inspector,
            new LegRunRequest(temp.Path, null, Json: true, UseStaged: useStaged) { Workload = new LegWorkload(Build: false, Test: false, ["rh-probe"]) });

        Assert.Equal("skipped-tool-missing", verdicts["here"].Verdict);
        Assert.StartsWith("local: 'rh-probe' is not installed there", verdicts["here"].Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run names where its records are - as 'logs:' in the text, as runDirectory in --json - so a
    /// caller never works out which tree a run wrote into. Run in a worktree, they are in the
    /// worktree: kept in the main checkout, a worktree's records were out of its reach.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ARun_NamesWhereItsRecordsAre_InTheTreeThatRanIt(bool json, bool inWorktree)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var tree = inWorktree ? temp.Combine("feature") : temp.Path;

        var outcome = await OutcomeAsync(
            temp,
            harness,
            OneLeg(harness),
            SshAndLocal(harness),
            new LegRunRequest(tree, null, Json: json) { Workload = LegWorkload.Copy },
            tree: tree);

        var directory = RunDirectoryOf(outcome, json);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.StartsWith(Path.Combine(tree, ".harness-config", "runs") + Path.DirectorySeparatorChar, directory, StringComparison.Ordinal);
        Assert.True(Directory.Exists(directory), $"the run named '{directory}', which it never made");
    }

    /// <summary>
    /// A run ended by a refusal after it made its directory still names it, and so does one refused
    /// because another run owns its log path: the text form of that one named nowhere at all.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ARunStoppedAfterItHadADirectory_StillNamesIt(bool json, bool logHeld)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        var outcome = await OutcomeAsync(
            temp,
            harness,
            OneLeg(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: json) { Workload = LegWorkload.Copy },
            work: _ => throw new HarnessException(HarnessExit.Refused, "the leg's configuration cannot be satisfied"),
            logs: logHeld ? new LogOwnership(new LogsOwnedElsewhere(harness.FileSystem), harness.Output, harness.Identity) : null);

        Assert.Equal(logHeld ? LegExit.LogHeld : HarnessExit.Refused, outcome.ExitCode);
        Assert.StartsWith(Path.Combine(temp.Path, ".harness-config", "runs") + Path.DirectorySeparatorChar, RunDirectoryOf(outcome, json), StringComparison.Ordinal);
    }

    /// <summary>
    /// A host running a leg for another machine names its records from its home, as <c>~</c>, in the ledger it
    /// answers with - where it keeps them, and why a run that owns them stopped it - and the same run typed on
    /// the machine itself names them in full. The machine that asked puts the host's words in its own output,
    /// and the home names the account the host was reached as.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AHostAnsweringAnotherMachine_NamesItsRecordsFromItsHome(bool servesAnotherMachine, bool logHeld)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory(home: servesAnotherMachine ? HostDoubles.HomeAt(temp.Path) : null);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            OneLeg(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy },
            logs: logHeld ? new LogOwnership(new LogsOwnedElsewhere(harness.FileSystem), harness.Output, harness.Identity) : null);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var runs = Path.Combine(servesAnotherMachine ? "~" : temp.Path, ".harness-config", "runs") + Path.DirectorySeparatorChar;

        Assert.StartsWith(runs, document.RootElement.GetProperty("runDirectory").GetString(), StringComparison.Ordinal);

        if (logHeld)
        {
            var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

            Assert.StartsWith($"another run owns '{runs}", document.RootElement.GetProperty("summary").GetString(), StringComparison.Ordinal);
            Assert.StartsWith($"another run owns '{runs}", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A leg another host ran was run there under a run of its own: its records are in that host's
    /// directory, which the run names beside its own, in the text and on the leg's JSON line.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALegAnotherHostRan_NamesThatHostsOwnDirectory(bool json)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        const string There = HostTree + "/.harness-config/runs/20260919-101500-0a1b2c3d";

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            ScriptedHostCommands.Answer(
                command,
                $$"""{"runDirectory": "{{There}}", "legs": [{"leg": "arm", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });

        var outcome = await OutcomeAsync(
            temp,
            harness,
            TwoLegs(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: json) { Workload = LegWorkload.Copy },
            hosts: hosts);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);

        if (json)
        {
            using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
            var arm = document.RootElement.GetProperty("legs").EnumerateArray().Single(leg => leg.GetProperty("leg").GetString() == "arm");

            Assert.Equal(There, arm.GetProperty("runDirectory").GetString());
        }
        else
        {
            Assert.Contains($"logs of arm on ssh {HostName}: {There}", outcome.Details ?? [], StringComparer.Ordinal);
        }
    }

    /// <summary>A run no leg could be placed for made no directory, and names none.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARunNoLegCanTake_NamesNoDirectory(bool json)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var elsewhere = harness.Platform.PlatformKey == "linux" ? "macos" : "linux";
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs = { ["elsewhere"] = HostDoubles.Leg(elsewhere, "x86_64") },
        };

        var outcome = await OutcomeAsync(
            temp, harness, config, SshAndLocal(harness), new LegRunRequest(temp.Path, null, Json: json) { Workload = LegWorkload.Copy });

        Assert.NotEqual(HarnessExit.Success, outcome.ExitCode);

        if (json)
        {
            using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
            Assert.False(document.RootElement.TryGetProperty("runDirectory", out _));
        }
        else
        {
            Assert.DoesNotContain(outcome.Details ?? [], line => line.StartsWith("logs:", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A run says, once it owns its own directory, each earlier run in its tree on this machine that ended holding one -
    /// most likely killed, or stopped with its machine, so it may have written no verdict - and releases it: nothing
    /// else would ever claim that directory again. The run's own verdict is untouched.
    /// </summary>
    [Fact]
    public async Task ARun_SaysAndReleases_AnEarlierRunThatEndedHoldingItsDirectory()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var dead = temp.Combine(".harness-config", "runs", "20250101-120000-deadbeef");

        Directory.CreateDirectory(dead);
        File.WriteAllText(
            LogOwnership.OwnerFile(dead),
            "{ \"machine\": \"" + Environment.MachineName + "\", \"processId\": " + (int.MaxValue - 1).ToString(CultureInfo.InvariantCulture)
            + ", \"processStamp\": \"a-process-that-has-gone\", \"runId\": \"20250101-120000-deadbeef\", \"takenUtc\": \"2025-01-01T12:00:00+00:00\" }");

        var outcome = await OutcomeAsync(
            temp, harness, OneLeg(harness), SshAndLocal(harness), new LegRunRequest(temp.Path, null) { Workload = LegWorkload.Copy });

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.False(File.Exists(LogOwnership.OwnerFile(dead)));
        Assert.Contains(
            $"logs: WARN - An earlier run was abandoned: pid {int.MaxValue - 1}, run 20250101-120000-deadbeef, since 2025-01-01 12:00:00Z",
            harness.StandardError.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A run another machine dispatched here leaves that machine the lines it says for itself - that the legs are
    /// starting, where each starts, and each one's verdict - since it relays every line said here, and each was said
    /// twice. A run typed here says them all.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARunDispatchedHere_LeavesTheDispatchingMachineTheLinesItSays(bool dispatched)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        var outcome = await OutcomeAsync(
            temp,
            harness,
            OneLeg(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true, Here: dispatched ? HostId.Local : null) { Workload = LegWorkload.Copy });

        var said = harness.StandardOutput.ToString() + harness.StandardError.ToString();

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(
            [!dispatched, !dispatched, !dispatched],
            new[] { "test: starting 1 leg(s) across", "test: native: starting on", "test: native: passed" }.Select(line => said.Contains(line, StringComparison.Ordinal)));
    }

    /// <summary>
    /// A run whose runs directory cannot be listed says so, and runs: nothing it decides depends on the runs beside its
    /// own directory, which it still gives up at the end.
    /// </summary>
    [Fact]
    public async Task ARun_WhoseRunsDirectoryCannotBeListed_SaysSo_AndRuns()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var runs = temp.Combine(".harness-config", "runs");
        var logs = new LogOwnership(
            new UnlistableDirectory(harness.FileSystem, runs, () => new IOException("The network path was not found.")),
            harness.Output,
            harness.Identity);

        var outcome = await OutcomeAsync(
            temp, harness, OneLeg(harness), SshAndLocal(harness), new LegRunRequest(temp.Path, null) { Workload = LegWorkload.Copy }, logs: logs);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Contains("logs: WARN - The runs beside '", harness.StandardError.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(runs, "*" + LogOwnership.OwnerSuffix));
    }

    /// <summary>
    /// Whatever goes wrong once a run owns its directory, the run gives it up: a failure nobody expected, while it looks
    /// beside that directory for runs that were abandoned, leaves no claim behind for the next run to call abandoned.
    /// </summary>
    [Fact]
    public async Task ARun_GivesUpItsDirectory_ThoughLookingBesideItFailedUnexpectedly()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var runs = temp.Combine(".harness-config", "runs");
        var logs = new LogOwnership(
            new UnlistableDirectory(harness.FileSystem, runs, () => new InvalidOperationException("a failure nobody expected")),
            harness.Output,
            harness.Identity);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => OutcomeAsync(
                temp, harness, OneLeg(harness), SshAndLocal(harness), new LegRunRequest(temp.Path, null) { Workload = LegWorkload.Copy }, logs: logs));

        Assert.Empty(Directory.GetFiles(runs, "*" + LogOwnership.OwnerSuffix));
    }

    /// <summary>
    /// A run makes the runs directory it writes into ignore itself, whatever the tree's own .gitignore
    /// says: a tree with no rule for it - a worktree of a branch that predates the harness - showed a
    /// run's records in git status, where the next 'git add -A' committed them.
    /// </summary>
    [Fact]
    public async Task ARun_KeepsItsRecordsOutOfGit_WhereTheTreeHasNoRuleForThem()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.RunGitAsync(temp.Path, ["init", "--quiet", "."], token);

        var outcome = await OutcomeAsync(temp, harness, OneLeg(harness), SshAndLocal(harness), new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy });
        var status = await harness.RunGitAsync(temp.Path, ["status", "--porcelain", "--untracked-files=all", "--", ".harness-config/runs"], token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.True(Directory.Exists(RunDirectoryOf(outcome, json: true)));
        Assert.Equal(string.Empty, status.StandardOutput.Trim());
        Assert.Equal(HarnessLayout.SelfIgnoreRule, File.ReadAllText(temp.Combine(".harness-config", "runs", ".gitignore")));
    }

    /// <summary>
    /// A leg whose toolchain names a developer environment runs in it: every process it starts is
    /// given what Visual Studio set up, over what the host declares - the host's own PATH kept behind
    /// Visual Studio's tools, its other variables untouched - and its line names the environment.
    /// </summary>
    [Fact]
    public async Task ALegWhoseToolchainNamesADeveloperEnvironment_RunsInIt_AndNamesIt()
    {
        using var temp = new TempDirectory();
        using var visualStudio = new ScriptedVisualStudio().Carrying("x64", "cl", "cmake");
        var harness = new HarnessFactory();
        IReadOnlyDictionary<string, string>? given = null;
        IReadOnlyDictionary<string, string>? built = null;

        var outcome = await OutcomeAsync(
            temp,
            harness,
            MsvcLeg(),
            WindowsHere(visualStudio),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly },
            work: leg =>
            {
                given = leg.Leg.Environment;
                built = leg.Leg.BuildRequestFor(leg.Context.Config, leg.RunDirectory).HostEnvironment;
                return new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed };
            },
            developerEnvironments: visualStudio.Provider());

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.NotNull(given);
        Assert.Equal(visualStudio.BinFor("x64") + Path.PathSeparator + @"D:\tools", given["PATH"]);
        Assert.Equal(visualStudio.Include, given["INCLUDE"]);
        Assert.Equal(@"D:\cache", given["CCACHE_DIR"]);
        Assert.Equal(given, built);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var environment = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("developerEnvironment");

        Assert.Equal("vs", environment.GetProperty("name").GetString());
        Assert.Equal(visualStudio.InstallationPath, environment.GetProperty("installationPath").GetString());
        Assert.Equal(ScriptedVisualStudio.ToolsVersion, environment.GetProperty("toolsVersion").GetString());
        Assert.Equal("amd64", environment.GetProperty("architecture").GetString());
        Assert.Contains("native: setting up developer environment 'vs'", harness.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            $"native: passed (developer environment: vs (Visual Studio {ScriptedVisualStudio.InstallationVersion}, MSVC {ScriptedVisualStudio.ToolsVersion}, amd64))",
            harness.StandardOutput.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A developer environment the survey found that will not set up where the leg runs fails the leg,
    /// saying why, before anything of it starts: as a program that will not start once a leg began
    /// does, never as a skip a gate accepting an incomplete run would pass.
    /// </summary>
    [Fact]
    public async Task ADeveloperEnvironmentThatWillNotSetUp_FailsItsLeg_BeforeAnythingOfItStarts()
    {
        using var temp = new TempDirectory();
        using var visualStudio = new ScriptedVisualStudio
        {
            ExitCode = "1",
            Log = System.Text.Encoding.Unicode.GetBytes("[ERROR:vcvarsall.bat] Invalid argument found : amd64\r\n"),
        };
        var harness = new HarnessFactory();
        var ran = new List<string>();

        var verdicts = await OutcomeAsync(
            temp,
            harness,
            MsvcLeg(),
            WindowsHere(visualStudio),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly },
            ran: leg => ran.Add(leg.Name),
            developerEnvironments: visualStudio.Provider());

        using var document = JsonDocument.Parse(Assert.Single(verdicts.Data));
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

        Assert.Equal("failed", leg.GetProperty("verdict").GetString());
        Assert.Equal(
            "developer environment 'vs': vcvarsall.bat amd64 exited 1: [ERROR:vcvarsall.bat] Invalid argument found : amd64",
            leg.GetProperty("detail").GetString());
        Assert.Empty(ran);
    }

    /// <summary>
    /// A program the leg starts that the PATH its developer environment set up does not hold - CMake,
    /// where Visual Studio's CMake component is not installed - skips the leg as a tool missing, named,
    /// before anything of it starts: the survey could not require it, and the run finds it missing then
    /// rather than halfway through a build. So it does where the host's own env sets a PATH, which the
    /// environment is set up over. Its line still names the environment it looked in.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProgramTheDeveloperEnvironmentLacks_SkipsItsLeg_NamingIt_BeforeAnythingOfItStarts(bool hostSetsPath)
    {
        using var temp = new TempDirectory();
        using var visualStudio = new ScriptedVisualStudio().Carrying("x64", "cl");
        var harness = new HarnessFactory();
        var ran = new List<string>();

        var outcome = await OutcomeAsync(
            temp,
            harness,
            MsvcLeg(hostSetsPath),
            WindowsHere(visualStudio),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly },
            ran: leg => ran.Add(leg.Name),
            developerEnvironments: visualStudio.Provider());

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

        Assert.Equal("skipped-tool-missing", leg.GetProperty("verdict").GetString());
        Assert.StartsWith(
            "'cmake' is not installed there: neither on the PATH developer environment 'vs' sets up nor in any directory searched for programs",
            leg.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("vs", leg.GetProperty("developerEnvironment").GetProperty("name").GetString());
        Assert.Empty(ran);
    }

    /// <summary>A copy starts nothing on the host, so the leg's developer environment is never set up for it.</summary>
    [Fact]
    public async Task ACopy_SetsUpNoDeveloperEnvironment()
    {
        using var temp = new TempDirectory();
        using var visualStudio = new ScriptedVisualStudio();
        var harness = new HarnessFactory();
        var inspector = WindowsHere(visualStudio);

        var verdicts = await RunAsync(
            temp,
            harness,
            MsvcLeg(),
            inspector,
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy },
            developerEnvironments: visualStudio.Provider());

        Assert.Equal("passed", verdicts["native"].Verdict);
        Assert.Empty(visualStudio.Probes);
        Assert.Empty(visualStudio.Captures);
        Assert.Empty(Assert.Single(inspector.DeveloperEnvironmentsAsked));
    }

    /// <summary>
    /// A heavy leg on a machine that declares admission waits for one of its slots before any of its work, and one that
    /// waited as long as the machine allows is not-admitted - exit 7, never failed - naming what held the slots, with
    /// nothing of it run.
    /// </summary>
    [Fact]
    public async Task AHeavyLegThatWaitedAsLongAsItsMachineAllows_IsNotAdmitted_NamingTheHolders_AndNothingOfItRuns()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        var ran = false;

        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "other"));

        var outcome = await OutcomeAsync(
            temp,
            harness,
            Admitting(OneLeg(harness), defaults: new AdmissionSettings { HeavyLegs = 1, MaxWaitMinutes = 1 }),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = Heavy },
            ran: _ => ran = true);

        Assert.Equal(LegExit.NotAdmitted, outcome.ExitCode);
        Assert.False(ran);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

        Assert.Equal("not-admitted", leg.GetProperty("verdict").GetString());
        Assert.StartsWith(
            "not admitted after 1m00s waiting for one of this machine's 1 heavy-leg slot(s), held by '/src/other'",
            leg.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.False(leg.GetProperty("admission").GetProperty("admitted").GetBoolean());
        Assert.Single(leg.GetProperty("admission").GetProperty("holders").EnumerateArray());
        Assert.Equal(["other"], AdmissionKit.Read(record).Select(entry => entry.Leg));
    }

    /// <summary>
    /// A heavy leg its machine took holds its slot while its work runs, gives it back as the work ends, and names on its
    /// line how long it waited and the memory it started at - in the table and in --json.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHeavyLegTaken_HoldsItsSlotWhileItsWorkRuns_AndNamesTheWaitAndTheMemory(bool json)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        IReadOnlyList<SlotEntry>? during = null;

        var outcome = await OutcomeAsync(
            temp,
            harness,
            Admitting(OneLeg(harness), local: new AdmissionSettings { HeavyLegs = 2 }),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: json) { Workload = Heavy },
            ran: _ => during = AdmissionKit.Read(record),
            admission: AdmissionKit.Admission(harness, record, new ScriptedGauge(12.5), new ManualClock()));

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("native", Assert.Single(during!).Leg);
        Assert.Empty(AdmissionKit.Read(record));

        if (json)
        {
            using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
            var admission = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("admission");

            Assert.True(admission.GetProperty("admitted").GetBoolean());
            Assert.Equal(0, admission.GetProperty("waitedSeconds").GetDouble());
            Assert.Equal(12.5, admission.GetProperty("memoryPercent").GetDouble());
            Assert.Equal("12.5% in use (12.5 of 100 by the test)", admission.GetProperty("memory").GetString());
        }
        else
        {
            Assert.Contains(outcome.Details ?? [], line => line.Contains("admitted at once, memory 12.5% in use (12.5 of 100 by the test)", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A heavy leg a host runs for the machine that dispatched it asks the host's slots under that machine's run, so the
    /// command's other legs there wait for its slot without that wait counting.
    /// </summary>
    [Fact]
    public async Task AHeavyLegRunForAnotherMachine_AsksItsSlotUnderThatMachinesRun()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        IReadOnlyList<SlotEntry>? during = null;

        var outcome = await OutcomeAsync(
            temp,
            harness,
            Admitting(OneLeg(harness), local: new AdmissionSettings { HeavyLegs = 2 }),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null) { Workload = Heavy },
            ran: _ => during = AdmissionKit.Read(record),
            admission: AdmissionKit.Admission(harness, record, new ScriptedGauge(12.5), new ManualClock()),
            origin: new CommandOrigin(ServesAnotherMachine: true, new Dispatch("20261008-120000-0a1b2c3d", null)));

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("20261008-120000-0a1b2c3d", Assert.Single(during!).RunId);
    }

    /// <summary>
    /// A heavy leg whose build's need its placement knew claims that room on this machine as it is admitted - read where its
    /// build directory is - holds it while its work runs and gives it back as the work ends, its line naming what it
    /// claimed. Counted only as a command placed its own legs, two commands each placing one leg on one host both found it
    /// room, and filled its disk between them.
    /// </summary>
    [Fact]
    public async Task AHeavyLegWhoseBuildNeedsRoom_ClaimsItAsItIsAdmitted_AndGivesItBackAsItsWorkEnds()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        var room = new ScriptedRoom(harness.FileSystem, 40 * AdmissionKit.Gibibyte);
        IReadOnlyList<RoomClaim>? during = null;

        var config = Admitting(
            new HarnessConfig
            {
                BuildConfigs = { ["debug"] = new BuildConfiguration() },
                Legs = { ["native"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug", BuildSpaceGiB = 8 } },
            },
            local: new AdmissionSettings { HeavyLegs = 2 });

        var inspector = new RecordingInspector(host => new HostReport { Host = host, Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor })
        {
            BuildRooms = (_, path) => new BuildDirectoryRoom(path, Exists: false, RecordedBytes: null, new DiskSpace(30L << 30, 100L << 30, "/data"), Unmeasured: null),
        };

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            inspector,
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildAndTest },
            ran: _ => during = AdmissionKit.ReadClaims(record),
            admission: AdmissionKit.Admission(harness, record, new ScriptedGauge(10), new ManualClock(), fileSystem: room));

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);

        var claim = Assert.Single(during!);

        Assert.Equal(("native", 8 * AdmissionKit.Gibibyte, "/data"), (claim.Holder.Leg, claim.Bytes, claim.Filesystem));
        Assert.Empty(AdmissionKit.ReadClaims(record));
        Assert.Equal(Assert.Single(inspector.RoomAsked).Room.Builds, room.Asked);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var admission = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("admission");

        Assert.Equal("its build needs ~8 GiB; 40 GiB free on '/data'", admission.GetProperty("room").GetString());
    }

    /// <summary>
    /// A leg that builds and tests nothing - a copy of the tree, as a repository guard's is - is light, and starts at
    /// once; and a machine that declares no admission takes every leg at once. Neither asks for a slot.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ALightLeg_OrAMachineDeclaringNoAdmission_AsksForNoSlot(bool heavy, bool declared)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);

        // Every slot the built-in count gives held, so a leg that asked would not be taken.
        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "other"), AdmissionKit.Holder(harness, "another"));

        var verdicts = await RunAsync(
            temp,
            harness,
            declared ? Admitting(OneLeg(harness), defaults: new AdmissionSettings { HeavyLegs = 1, MaxWaitMinutes = 1 }) : OneLeg(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = heavy ? Heavy : LegWorkload.Copy });

        Assert.Equal("passed", verdicts["native"].Verdict);
        Assert.Equal(["other", "another"], AdmissionKit.Read(record).Select(entry => entry.Leg));
    }

    /// <summary>
    /// A step heavy only where it runs - limited by runOn - makes heavy the legs of those systems alone: a leg of another
    /// system, which never runs it, asks for no slot, and one of a system it runs on waits for one as any heavy leg does.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStepHeavyOnlyWhereItRuns_MakesHeavyTheLegsOfThoseSystemsAlone(bool runsHere)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        var elsewhere = harness.Platform.PlatformKey == PlatformNames.Linux ? PlatformNames.Windows : PlatformNames.Linux;
        var os = runsHere ? harness.Platform.PlatformKey : elsewhere;

        // The one slot held, so a leg that asked would not be taken.
        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "other"));

        var verdicts = await RunAsync(
            temp,
            harness,
            Admitting(OneLeg(harness), defaults: new AdmissionSettings { HeavyLegs = 1, MaxWaitMinutes = 1 }),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true)
            {
                Workload = LegWorkload.Copy with { HeavyOnlyOn = [os] },
            });

        Assert.Equal(runsHere ? "not-admitted" : "passed", verdicts["native"].Verdict);
    }

    /// <summary>
    /// A step naming what the build makes, limited by runOn, builds - and so makes heavy - the legs of those systems
    /// alone: a leg of this machine's system waits for the slot held here, and one of another's starts at once.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStepBuildingOnlyWhereItRuns_MakesHeavyTheLegsOfThoseSystemsAlone(bool runsHere)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        var elsewhere = harness.Platform.PlatformKey == PlatformNames.Linux ? PlatformNames.Windows : PlatformNames.Linux;
        var step = new ActionStep { Name = "deps", WorkingDirectory = "{buildDir}", RunOn = [runsHere ? harness.Platform.PlatformKey : elsewhere] };

        // The one slot held, so a leg that asked would not be taken.
        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "other"));

        var verdicts = await RunAsync(
            temp,
            harness,
            Admitting(OneLeg(harness), defaults: new AdmissionSettings { HeavyLegs = 1, MaxWaitMinutes = 1 }),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true)
            {
                Workload = LegWorkload.Copy with { BuiltBy = [BuildCause.Of(step)] },
            });

        Assert.Equal(runsHere ? "not-admitted" : "passed", verdicts["native"].Verdict);
    }

    /// <summary>
    /// A WSL distribution runs on this machine, so this machine takes its heavy legs - by hosts.local's rule, against its
    /// own slots - before dispatching them; an ssh host is a machine of its own, and takes its legs itself, whose line
    /// comes back naming how. Measured: four worktrees' builds on one Windows machine and its distribution drove its commit
    /// to 81 of 113.7 GiB.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWslLegIsTakenByThisMachine_AndAnSshLegByItsHost(bool wsl)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        var asked = false;

        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "other"));

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            // Declared for every machine as well, so a leg this machine took that it should not have is refused here.
            Defaults = new HarnessDefaults { Admission = new AdmissionSettings { HeavyLegs = 1, MaxWaitMinutes = 1 } },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig { Admission = new AdmissionSettings { HeavyLegs = 1, MaxWaitMinutes = 1 } },
                Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" } },
                Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree } },
            },
            Legs =
            {
                ["remote"] = wsl
                    ? new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Wsl = "Ubuntu" }
                    : new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = HostName },
            },
        };

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            asked = true;
            ScriptedHostCommands.Answer(
                command,
                """{"legs": [{"leg": "remote", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1, "admission": {"admitted": true, "waitedSeconds": 42, "memoryPercent": 50.5, "memory": "50.5% in use (on the host)"}}]}""");

            return HostResults.Finished(command, 0);
        });

        var inspector = new RecordingInspector(host => new HostReport
        {
            Host = host,
            Os = host.Kind == HostKind.Local ? harness.Platform.PlatformKey : "linux",
            Processor = host.Kind == HostKind.Ssh ? "arm64" : "x86_64",
            Session = host.Kind == HostKind.Local ? null : Session(host),
        });

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            inspector,
            new LegRunRequest(temp.Path, null, Json: true) { Workload = Heavy },
            hosts: hosts);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

        if (wsl)
        {
            Assert.Equal("not-admitted", leg.GetProperty("verdict").GetString());
            Assert.False(asked);
        }
        else
        {
            Assert.Equal("passed", leg.GetProperty("verdict").GetString());
            Assert.Equal(42, leg.GetProperty("admission").GetProperty("waitedSeconds").GetDouble());
            Assert.True(asked);
        }

        Assert.Equal(["other"], AdmissionKit.Read(record).Select(entry => entry.Leg));
    }

    /// <summary>
    /// A host running a leg another machine dispatched to it takes it by its own section, as a machine of its own; a WSL
    /// distribution running one takes nothing, since the machine that dispatched it - the one it runs on - did.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHostSentALeg_TakesItByItsOwnSection_AndADistributionNever(bool wsl)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        var platform = harness.Platform;
        var ran = false;

        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "other"));

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig
            {
                // This machine's own rule would refuse the leg too: a distribution sent one asks by neither.
                Local = new LocalHostConfig { Admission = new AdmissionSettings { HeavyLegs = 1, MaxWaitMinutes = 1 } },
                Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" } },
                Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree, Admission = new AdmissionSettings { HeavyLegs = 1, MaxWaitMinutes = 1 } } },
            },
            Legs = { ["native"] = HostDoubles.Leg(platform.PlatformKey, platform.Processor) },
        };

        var verdicts = await RunAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true, Here: wsl ? HostId.Wsl("Ubuntu") : HostId.Ssh(HostName)) { Workload = Heavy },
            ran: _ => ran = true);

        Assert.Equal(wsl ? "passed" : "not-admitted", verdicts["native"].Verdict);
        Assert.Equal(wsl, ran);
    }

    /// <summary>
    /// A heavy leg about to wait on this machine's memory has WSL's page cache dropped for this repository - whose WSL
    /// hosts name the distributions this tool may run a command in - and its line says what came back.
    /// </summary>
    [Fact]
    public async Task AHeavyLegAboutToWaitOnTheMemory_HasWslsPageCacheDroppedForThisRepository()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cache = new ScriptedPageCache(PageCacheDrop.Dropped(HostId.Wsl("Ubuntu"), 4 * AdmissionKit.Gibibyte));
        var config = Admitting(OneLeg(harness), local: new AdmissionSettings { HeavyLegs = 2 });

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null) { Workload = Heavy },
            admission: AdmissionKit.Admission(harness, AdmissionRecord(temp), new ScriptedGauge(90, 50), new ManualClock()),
            pageCache: cache);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Same(config, Assert.Single(cache.Asked).Config);
        Assert.Contains(
            "native: WSL's page cache was dropped as root in wsl Ubuntu, 4 GiB of it, and 1m00s later the memory read 50.0% in use "
            + "(50 of 100 by the test), from 90.0% in use (90 of 100 by the test)",
            harness.StandardOutput.ToString() + harness.StandardError.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A WSL leg this machine sent has WSL's page cache dropped as it ends - what its build read and wrote, which this
    /// machine counts as in use until the virtual machine idles for minutes - where this machine admits its heavy legs by
    /// the memory, and its line says so: without waiting for what comes back, which the next leg's wait reads. A leg of an
    /// ssh host, and one where this machine admits nothing, drop none.
    /// </summary>
    [Theory]
    [InlineData("wsl", true, true)]
    [InlineData("wsl", false, false)]
    [InlineData("ssh", true, false)]
    public async Task AWslLegsEnd_DropsWslsPageCache_WhereThisMachineAdmitsByTheMemory(string kind, bool admits, bool drops)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cache = new ScriptedPageCache(PageCacheDrop.Dropped(HostId.Wsl("Ubuntu"), AdmissionKit.Gibibyte));

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig { Admission = admits ? new AdmissionSettings() : null },
                Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" } },
                Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree } },
            },
            Legs =
            {
                ["remote"] = kind == "wsl"
                    ? new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Wsl = "Ubuntu" }
                    : new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Ssh = HostName },
            },
        };

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            ScriptedHostCommands.Answer(command, """{"legs": [{"leg": "remote", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });

        var inspector = new RecordingInspector(host => new HostReport
        {
            Host = host,
            Os = host.Kind == HostKind.Local ? harness.Platform.PlatformKey : "linux",
            Processor = host.Kind == HostKind.Local ? harness.Platform.Processor : "x86_64",
            Session = host.Kind == HostKind.Local ? null : Session(host),
        });

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            inspector,
            new LegRunRequest(temp.Path, null, Json: true) { Workload = Heavy },
            hosts: hosts,
            pageCache: cache);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(drops ? [config] : [], cache.Asked.Select(context => context.Config));
        Assert.Equal(
            drops,
            (harness.StandardOutput.ToString() + harness.StandardError.ToString()).Contains(
                "remote: as the leg ended, WSL's page cache was dropped as root in wsl Ubuntu, 1 GiB of it",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// A heavy leg's builds here - and a sweep's, each of whose workers builds - are held to the room its machine keeps
    /// free, admission.minFreeGiB, where that machine declares admission: 2 GiB where it says nothing. A light leg's are held
    /// to none, nor are those of a leg whose machine declares no admission, or keeps nothing free.
    /// </summary>
    [Theory]
    [InlineData("heavy", true, null, 2L << 30)]
    [InlineData("sweep", true, null, 2L << 30)]
    [InlineData("heavy", true, 0.5, 1L << 29)]
    [InlineData("heavy", true, 0.0, null)]
    [InlineData("heavy", false, null, null)]
    [InlineData("light", true, null, null)]
    public async Task ALegsBuildsHere_AreHeldToTheRoomItsMachineKeepsFree_WhereItIsHeavyAndTheMachineAdmits(
        string kind,
        bool admits,
        double? minFreeGiB,
        long? floor)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var ran = false;
        RoomFloor? held = null;
        var config = admits ? Admitting(OneLeg(harness), local: new AdmissionSettings { MinFreeGiB = minFreeGiB }) : OneLeg(harness);
        var workload = kind switch
        {
            "heavy" => Heavy,
            "sweep" => Sweep,
            _ => LegWorkload.Copy,
        };

        await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null) { Workload = workload },
            ran: leg => (ran, held) = (true, leg.Floor));

        Assert.True(ran);
        Assert.Equal(floor, held?.Bytes);
        Assert.Empty(held?.Also ?? []);
        Assert.Null(held?.Unwatched);
    }

    /// <summary>
    /// A WSL distribution's leg holds its builds to the floor on the drive its disk grows on as well, through that drive's
    /// mount in the distribution, as the machine that sent it named the drive: the distribution's own room is its virtual
    /// disk's, which a full drive does not shrink. Where that machine named none, or the drive is mounted nowhere there,
    /// the build is told why only the distribution's own room is held.
    /// </summary>
    [Theory]
    [InlineData("C:\\", "C:\\134 /mnt/c 9p rw,noatime,aname=drvfs;path=C:\\;uid=1000 0 0", "/mnt/c", null)]
    [InlineData("c:", "none /mnt/wsl tmpfs rw 0 0\nC:\\134 /mnt/my\\040c 9p rw 0 0", "/mnt/my c", null)]
    [InlineData(
        null,
        "C:\\134 /mnt/c 9p rw 0 0",
        null,
        "the machine that sent this leg named no drive where WSL keeps this distribution's disk, so only the distribution's own room is "
        + "held to admission.minFreeGiB")]
    [InlineData(
        "D:\\",
        "C:\\134 /mnt/c 9p rw 0 0",
        null,
        "the drive where WSL keeps this distribution's disk, D:\\, could not be found here, so only the distribution's own room is held to "
        + "admission.minFreeGiB: '/proc/mounts' lists no mount of it")]
    public async Task AWslLegsBuilds_AreHeldToTheFloorOnTheDriveItsDiskGrowsOn_ThroughItsMountThere(
        string? drive,
        string mounts,
        string? mount,
        string? unwatched)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        RoomFloor? held = null;
        var config = Admitting(OneLeg(harness), local: new AdmissionSettings());

        config.Hosts.Wsl["Ubuntu"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" };

        await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true, Here: HostId.Wsl("Ubuntu")) { Workload = Heavy },
            ran: leg => held = leg.Floor,
            origin: new CommandOrigin(ServesAnotherMachine: true, new Dispatch(RunId.New().Value, drive)),
            fileSystem: new MountsListing(harness.FileSystem, mounts));

        Assert.NotNull(held);
        Assert.Equal(2L << 30, held.Bytes);
        Assert.Equal(mount is null ? Array.Empty<(string, string)>() : [(mount, ", where WSL keeps its disk")], held.Also);
        Assert.Equal(unwatched, held.Unwatched);
    }

    /// <summary>The real file system, except that what is mounted reads as <paramref name="mounts"/> says.</summary>
    private sealed class MountsListing(IFileSystem inner, string mounts) : PassThroughFileSystem(inner)
    {
        public override string ReadAllText(string path)
            => path == WindowsDriveMounts.MountsFile ? mounts : base.ReadAllText(path);
    }

    /// <summary>
    /// A heavy leg whose work throws gives its slot back all the same: kept, the process that asked would stay alive,
    /// and every later heavy leg on the machine - this run's and every other command's - would wait for it in vain.
    /// </summary>
    [Fact]
    public async Task AHeavyLegWhoseWorkThrows_GivesItsSlotBack()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        var config = Admitting(OneLeg(harness), defaults: new AdmissionSettings { HeavyLegs = 1, MaxWaitMinutes = 1 });

        var thrown = await RunAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = Heavy },
            ran: _ => throw new InvalidOperationException("the leg's work failed as nothing else does"));

        Assert.Equal("poisoned", thrown["native"].Verdict);
        Assert.Empty(AdmissionKit.Read(record));

        var again = await RunAsync(temp, harness, config, SshAndLocal(harness), new LegRunRequest(temp.Path, null, Json: true) { Workload = Heavy });

        Assert.Equal("passed", again["native"].Verdict);
    }

    /// <summary>
    /// A WSL leg this machine took holds this machine's slot for as long as the distribution runs it, and names the
    /// memory this machine read - the distribution's own figures do not show this machine's memory.
    /// </summary>
    [Fact]
    public async Task AWslLegTakenHere_HoldsItsSlotWhileTheDistributionRunsIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        IReadOnlyList<SlotEntry>? during = null;

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig { Admission = new AdmissionSettings { HeavyLegs = 2 } },
                Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" } },
            },
            Legs = { ["remote"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Wsl = "Ubuntu" } },
        };

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            during = AdmissionKit.Read(record);
            ScriptedHostCommands.Answer(command, """{"legs": [{"leg": "remote", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });

        var inspector = new RecordingInspector(host => new HostReport
        {
            Host = host,
            Os = host.Kind == HostKind.Local ? harness.Platform.PlatformKey : "linux",
            Processor = "x86_64",
            Session = host.Kind == HostKind.Local ? null : Session(host),
        });

        var outcome = await OutcomeAsync(temp, harness, config, inspector, new LegRunRequest(temp.Path, null, Json: true) { Workload = Heavy }, hosts: hosts);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

        Assert.Equal("passed", leg.GetProperty("verdict").GetString());
        Assert.Equal("remote", Assert.Single(during!).Leg);
        Assert.Empty(AdmissionKit.Read(record));
        Assert.Equal(10, leg.GetProperty("admission").GetProperty("memoryPercent").GetDouble());
        Assert.Equal(Path.GetFullPath(record), leg.GetProperty("admission").GetProperty("record").GetString());
    }

    /// <summary>
    /// A command admitting each unit of its legs' work - a sweep of mutation arms - holds no slot for the leg: each unit
    /// asks its machine as it starts, named after its leg, holds its slot until it is disposed, and gives it back; a unit
    /// asking not to settle is taken on one reading beside its sweep's other units. Every unit's line of progress is its
    /// leg's.
    /// </summary>
    [Fact]
    public async Task AWorkloadAdmittingEachUnit_HoldsNoSlotForTheLeg_AndAdmitsEachUnitByName()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        var clock = new ManualClock();
        IReadOnlyList<SlotEntry>? beforeAnyUnit = null;
        IReadOnlyList<SlotEntry>? withBoth = null;
        IReadOnlyList<SlotEntry>? afterTheFirst = null;

        var outcome = await OutcomeAsync(
            temp,
            harness,
            Admitting(OneLeg(harness), local: new AdmissionSettings { HeavyLegs = 2, SettleSeconds = [90, 90] }),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = Sweep },
            admission: AdmissionKit.Admission(harness, record, new ScriptedGauge(10), clock, settle: TimeSpan.FromSeconds(90)),
            workAsync: async (work, token) =>
            {
                beforeAnyUnit = AdmissionKit.Read(record);

                using (var first = await work.AdmitUnit(new UnitAdmission("first-arm"), token))
                {
                    using var second = await work.AdmitUnit(new UnitAdmission("second-arm", Settle: false), token);

                    withBoth = AdmissionKit.Read(record);
                    Assert.True(first?.Fact.Admitted);
                    Assert.True(second?.Fact.Admitted);
                }

                afterTheFirst = AdmissionKit.Read(record);
                work.Progress("swept");

                return new LegEntry { Leg = work.Leg.Name, Verdict = LegVerdict.Passed };
            });

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Empty(beforeAnyUnit!);
        Assert.Equal(["native/first-arm", "native/second-arm"], withBoth!.Select(entry => entry.Leg));
        Assert.Empty(afterTheFirst!);
        Assert.Empty(AdmissionKit.Read(record));
        Assert.Equal(TimeSpan.Zero, clock.Moved);
        Assert.Contains("native: swept", harness.StandardOutput.ToString() + harness.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A unit whose work needs room - a worker's copy made, and its first build - claims it as it is taken, by its own
    /// name, and gives it back as it is disposed.
    /// </summary>
    [Fact]
    public async Task AUnitNeedingRoom_ClaimsItAsItIsTaken_AndGivesItBack()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        var room = new ScriptedRoom(harness.FileSystem, 40 * AdmissionKit.Gibibyte);
        IReadOnlyList<RoomClaim>? during = null;

        var outcome = await OutcomeAsync(
            temp,
            harness,
            Admitting(OneLeg(harness), local: new AdmissionSettings { HeavyLegs = 2 }),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = Sweep },
            admission: AdmissionKit.Admission(harness, record, new ScriptedGauge(10), new ManualClock(), fileSystem: room),
            workAsync: async (work, token) =>
            {
                using (await work.AdmitUnit(new UnitAdmission("worker-1", AdmissionKit.Room(3)), token))
                {
                    during = AdmissionKit.ReadClaims(record);
                }

                return new LegEntry { Leg = work.Leg.Name, Verdict = LegVerdict.Passed };
            });

        var claim = Assert.Single(during!);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(("native/worker-1", 3 * AdmissionKit.Gibibyte), (claim.Holder.Leg, claim.Bytes));
        Assert.Empty(AdmissionKit.ReadClaims(record));
    }

    /// <summary>
    /// A WSL leg of a command admitting each unit is taken whole by this machine, which the distribution's memory figures
    /// cannot show, and holds its slot while the distribution sweeps it; a distribution sent such a leg asks nothing for
    /// its units, which this machine's slot already covers.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWslLegOfAWorkloadAdmittingEachUnit_IsTakenWholeByThisMachine_AndItsUnitsAskNothingThere(bool inTheDistribution)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        IReadOnlyList<SlotEntry>? during = null;
        Admission? unit = null;
        var asked = false;

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig { Admission = new AdmissionSettings { HeavyLegs = 2 } },
                Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" } },
            },
            Legs = inTheDistribution
                ? new(StringComparer.OrdinalIgnoreCase) { ["native"] = HostDoubles.Leg(harness.Platform.PlatformKey, harness.Platform.Processor) }
                : new(StringComparer.OrdinalIgnoreCase) { ["remote"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Wsl = "Ubuntu" } },
        };

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            during = AdmissionKit.Read(record);
            ScriptedHostCommands.Answer(command, """{"legs": [{"leg": "remote", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });

        var inspector = new RecordingInspector(host => new HostReport
        {
            Host = host,
            Os = host.Kind == HostKind.Local ? harness.Platform.PlatformKey : "linux",
            Processor = host.Kind == HostKind.Local ? harness.Platform.Processor : "x86_64",
            Session = host.Kind == HostKind.Local ? null : Session(host),
        });

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            inspector,
            new LegRunRequest(temp.Path, null, Json: true, Here: inTheDistribution ? HostId.Wsl("Ubuntu") : null) { Workload = Sweep },
            hosts: hosts,
            workAsync: async (work, token) =>
            {
                asked = true;
                unit = await work.AdmitUnit(new UnitAdmission("arm"), token);
                during = AdmissionKit.Read(record);

                return new LegEntry { Leg = work.Leg.Name, Verdict = LegVerdict.Passed };
            });

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(inTheDistribution ? [] : ["remote"], during!.Select(entry => entry.Leg));

        // The leg's work runs where the leg is: in the distribution its unit asks, and nothing there takes it; the machine
        // that sent the leg runs none of its work, so no unit of it asks here at all.
        Assert.Equal(inTheDistribution, asked);
        Assert.Null(unit);
        Assert.Empty(AdmissionKit.Read(record));
    }

    /// <summary>
    /// A host sent a leg of such a command admits each unit by the admission its own entry among the hosts declares:
    /// what the machine that typed the command declares for itself says nothing of this one's memory, so a host whose
    /// entry declares none asks nothing, whatever that machine declares.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AHostSentALegOfAWorkloadAdmittingEachUnit_AdmitsEachByItsOwnSettings(bool declared)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        var admission = new AdmissionSettings { HeavyLegs = 2 };
        IReadOnlyList<SlotEntry>? withBoth = null;
        var taken = new List<bool>();

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig { Admission = declared ? null : admission },
                Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree, Admission = declared ? admission : null } },
            },
            Legs = { ["arm"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug", Ssh = HostName } },
        };

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true, Here: HostId.Ssh(HostName)) { Workload = Sweep },
            workAsync: async (work, token) =>
            {
                using var first = await work.AdmitUnit(new UnitAdmission("first-arm"), token);
                using var second = await work.AdmitUnit(new UnitAdmission("second-arm", Settle: false), token);

                taken.AddRange([first is not null, second is not null]);
                withBoth = AdmissionKit.Read(record);

                return new LegEntry { Leg = work.Leg.Name, Verdict = LegVerdict.Passed };
            });

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal([declared, declared], taken);
        Assert.Equal(declared ? ["arm/first-arm", "arm/second-arm"] : [], withBoth!.Select(entry => entry.Leg));
        Assert.Empty(AdmissionKit.Read(record));
    }

    /// <summary>
    /// A command keyed apart - a sweep, whose work never touches the leg's tree - takes the lock its request chooses
    /// instead of the build's: a build of the same leg holding its variant never keeps it off, and another run holding
    /// the chosen key does, naming it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARequestsOwnLock_ReplacesTheBuildsLock(bool sweepHeld)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var config = OneLeg(harness);
        var variant = VariantKey.For(config, config.Legs["native"], harness.Platform.PlatformKey);

        var build = PlacedLeg.BuildLock(HostId.Local, temp.Path, variant, RunId.New(), "build");
        var sweep = MutationWorkers.SweepLock(HostId.Local, temp.Path, variant, RunId.New(), "check-mutations");

        await using var held = await runLock.AcquireAsync(layout, sweepHeld ? sweep : build, TestContext.Current.CancellationToken);

        var verdicts = await RunAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = Sweep, Lock = MutationWorkers.SweepLock },
            runLock);

        Assert.Equal(sweepHeld ? "refused-locked" : "passed", verdicts["native"].Verdict);

        if (sweepHeld)
        {
            Assert.Contains(held.Entry.Holder.RunId, verdicts["native"].Detail, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A record of the machine's heavy legs that cannot be read refuses the run, naming it: never a leg reported
    /// not-admitted as though the machine were busy, and never one started as though it were free.
    /// </summary>
    [Fact]
    public async Task ARecordOfTheHeavyLegsThatCannotBeRead_RefusesTheRun()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = AdmissionRecord(temp);
        var ran = false;

        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        File.WriteAllText(record, "not a record");

        var outcome = await OutcomeAsync(
            temp,
            harness,
            Admitting(OneLeg(harness), defaults: new AdmissionSettings { HeavyLegs = 1 }),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = Heavy },
            ran: _ => ran = true);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Contains(Path.GetFullPath(record), outcome.Message, StringComparison.Ordinal);
        Assert.False(ran);
    }

    /// <summary>
    /// A file edited after the run began never reaches a host's copy as the run's tree. The run reads the tree before
    /// any leg's work, and the copy is made from that reading: an edit put back before the sync came round leaves no
    /// trace there, and one the file still holds when it is to be carried makes the legs on that copy inputs-moved,
    /// with nothing of them run. The edit is made by this machine's leg, which runs first, so a tree read as each sync
    /// came round rather than as the run began would have carried it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFileEditedAfterTheRunBegan_NeverReachesAHostsCopyAsTheRunsTree(bool leftEdited)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        var copy = SyncKit.CopyPath(temp);
        var source = Path.Combine(temp.Path, "src", "a.c");
        string? tested = null;

        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "a\n", token);

        // A leg here and one on the host, one leg at a time, this machine's first.
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Defaults = new HarnessDefaults { MaxParallelLegsTotal = 1 },
            Hosts = new HostsConfig { Ssh = { [HostName] = new SshHostConfig { RepositoryPath = copy } } },
            Legs =
            {
                ["a-here"] = HostDoubles.Leg(harness.Platform.PlatformKey, harness.Platform.Processor),
                ["b-arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = HostName },
            },
        };

        await harness.InitializeHarnessAsync(temp.Path, token, config);
        await harness.CommitAllAsync(temp.Path, "initial", token);

        var transports = Substitute.For<ISyncTransportFactory>();
        transports.For(Arg.Any<HostReport>())
            .Returns(call => new RecordingTransport(SyncKit.Transport(harness), reports: call.Arg<HostReport>().Host));

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            tested = File.ReadAllText(Path.Combine(copy, "src", "a.c"));
            ScriptedHostCommands.Answer(command, """{"legs": [{"leg": "b-arm", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });

        try
        {
            // An edit of the same size, as a flipped operator is.
            var outcome = await OutcomeAsync(
                temp,
                harness,
                config,
                SshAndLocal(harness),
                new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy },
                sync: SyncKit.Service(harness, loader: HostDoubles.Loader(config, temp.Path, temp.Path), transports: transports),
                work: leg =>
                {
                    File.WriteAllText(source, "A\n");

                    if (!leftEdited)
                    {
                        File.WriteAllText(source, "a\n");
                    }

                    return new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed };
                },
                hosts: hosts,
                transports: transports);

            var legs = Verdicts(outcome);

            Assert.Equal("passed", legs["a-here"].Verdict);

            if (leftEdited)
            {
                Assert.Equal("inputs-moved", legs["b-arm"].Verdict);
                Assert.Equal(SyncKit.Moved(HostId.Ssh(HostName), "src/a.c", "changed", copy), legs["b-arm"].Detail);
                Assert.Null(tested);
            }
            else
            {
                Assert.Equal("passed", legs["b-arm"].Verdict);
                Assert.Equal("a\n", tested);
            }
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A tree that moved stops the copy it reached, and that one alone: another host's copy of the same tree, made once
    /// the file held what was read again, is the tree the run began with, and its leg runs; so does this machine's.
    /// </summary>
    [Fact]
    public async Task ATreeThatMoved_StopsOnlyTheCopyItReached()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(temp.Path, "src", "a.c");
        var tested = new Dictionary<string, string>(StringComparer.Ordinal);
        var copies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pi"] = SyncKit.CopyPath(temp),
            ["pj"] = SyncKit.CopyPath(temp),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "a\n", token);

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Defaults = new HarnessDefaults { MaxParallelLegsTotal = 1 },
            Hosts = new HostsConfig
            {
                Ssh =
                {
                    ["pi"] = new SshHostConfig { RepositoryPath = copies["pi"] },
                    ["pj"] = new SshHostConfig { RepositoryPath = copies["pj"] },
                },
            },
            Legs =
            {
                ["a-here"] = HostDoubles.Leg(harness.Platform.PlatformKey, harness.Platform.Processor),
                ["b-pi"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = "pi" },
                ["c-pj"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = "pj" },
            },
        };

        await harness.InitializeHarnessAsync(temp.Path, token, config);
        await harness.CommitAllAsync(temp.Path, "initial", token);

        // The edit is there as pi's copy is made, and gone again by pj's.
        var transports = Substitute.For<ISyncTransportFactory>();
        transports.For(Arg.Any<HostReport>()).Returns(call =>
        {
            var host = call.Arg<HostReport>().Host;
            File.WriteAllText(source, host.Name == "pi" ? "A\n" : "a\n");

            return new RecordingTransport(SyncKit.Transport(harness), reports: host);
        });

        var hosts = new ScriptedHostCommands((connection, command) =>
        {
            var leg = connection.Host.Name == "pi" ? "b-pi" : "c-pj";
            tested[leg] = File.ReadAllText(Path.Combine(copies[connection.Host.Name], "src", "a.c"));
            ScriptedHostCommands.Answer(command, $$"""{"legs": [{"leg": "{{leg}}", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });

        try
        {
            var outcome = await OutcomeAsync(
                temp,
                harness,
                config,
                SshAndLocal(harness),
                new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy },
                sync: SyncKit.Service(harness, loader: HostDoubles.Loader(config, temp.Path, temp.Path), transports: transports),
                hosts: hosts,
                transports: transports);

            var legs = Verdicts(outcome);

            Assert.Equal("passed", legs["a-here"].Verdict);
            Assert.Equal("inputs-moved", legs["b-pi"].Verdict);
            Assert.Equal("passed", legs["c-pj"].Verdict);
            Assert.Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["c-pj"] = "a\n" }, tested);
        }
        finally
        {
            foreach (var copy in copies.Values)
            {
                SyncKit.DeleteIfPresent(copy);
            }
        }
    }

    /// <summary>A leg that builds and tests nothing but is heavy all the same, as a runner saying so makes it.</summary>
    private static LegWorkload Heavy => new(Build: false, Test: false, []) { DeclaredHeavy = true };

    /// <summary>What a sweep of mutation arms has each leg do: build, in workers of its own, admitting each arm alone.</summary>
    private static LegWorkload Sweep => LegWorkload.BuildOnly with { AdmitsEachUnit = true };

    /// <summary>The record of the heavy legs a test's admission keeps: never this machine's own.</summary>
    private static string AdmissionRecord(TempDirectory temp) => temp.Combine("state", "admission.json");

    /// <summary><paramref name="config"/> with admission declared under defaults, or under hosts.local.</summary>
    private static HarnessConfig Admitting(HarnessConfig config, AdmissionSettings? defaults = null, AdmissionSettings? local = null) => new()
    {
        BuildConfigs = config.BuildConfigs,
        Legs = config.Legs,
        Defaults = new HarnessDefaults { Admission = defaults },
        Hosts = new HostsConfig { Local = new LocalHostConfig { Admission = local } },
    };

    /// <summary>
    /// A Windows leg built with msvc, whose toolchain names the Visual Studio environment, on a
    /// machine whose own env declares a compiler cache - and a PATH, unless <paramref name="hostSetsPath"/>
    /// says it declares none.
    /// </summary>
    private static HarnessConfig MsvcLeg(bool hostSetsPath = true) => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Projects = { new ProjectConfig { Name = "app", Type = "cmake", Path = "." } },
        DeveloperEnvironments = { ["vs"] = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio } },
        Toolchains =
        {
            ["msvc"] = new ToolchainConfig { Platforms = ["windows"], Env = { ["CC"] = "cl" }, DeveloperEnvironment = "vs" },
        },
        Hosts = new HostsConfig
        {
            Local = new LocalHostConfig
            {
                Env = hostSetsPath
                    ? new(StringComparer.OrdinalIgnoreCase) { ["Path"] = @"D:\tools", ["CCACHE_DIR"] = @"D:\cache" }
                    : new(StringComparer.OrdinalIgnoreCase) { ["CCACHE_DIR"] = @"D:\cache" },
            },
        },
        Legs = { ["native"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Toolchain = "msvc" } },
    };

    /// <summary>This machine as a Windows x86_64 one, where the survey finds <paramref name="visualStudio"/>.</summary>
    private static RecordingInspector WindowsHere(ScriptedVisualStudio visualStudio) => new(host => new HostReport
    {
        Host = host,
        Os = "windows",
        Processor = "x86_64",
        DeveloperEnvironments = new Dictionary<string, DeveloperEnvironmentCheck>(StringComparer.OrdinalIgnoreCase) { ["vs"] = visualStudio.Found },
    });

    /// <summary>
    /// Developer environments that must never be set up: a test that declares none has no business
    /// reaching Visual Studio.
    /// </summary>
    private static DeveloperEnvironmentProvider NoDeveloperEnvironment(HarnessFactory harness)
    {
        var processes = Substitute.For<IProcessRunner>();
        processes.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("This test declares no developer environment, so none is set up."));

        return new DeveloperEnvironmentProvider(harness.Platform, processes, harness.FileSystem, harness.Output);
    }

    /// <summary>A leg on this machine.</summary>
    private static HarnessConfig OneLeg(HarnessFactory harness) => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Legs = { ["native"] = HostDoubles.Leg(harness.Platform.PlatformKey, harness.Platform.Processor) },
    };

    /// <summary>The run directory an outcome names, read the way its caller reads it.</summary>
    private static string RunDirectoryOf(CommandOutcome outcome, bool json)
    {
        if (json)
        {
            using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
            return document.RootElement.GetProperty("runDirectory").GetString()!;
        }

        var logs = Assert.Single(outcome.Details ?? [], line => line.StartsWith("logs: ", StringComparison.Ordinal));
        return logs["logs: ".Length..];
    }

    /// <summary>The real file system, except that every log path reads as owned by a run on another machine.</summary>
    private sealed class LogsOwnedElsewhere(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        private static readonly string Owner = JsonSerializer.Serialize(
            new LogOwner("another-machine", 4242, "20260101-000000-00000000", DateTimeOffset.UnixEpoch),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        public override bool FileExists(string path)
            => path.EndsWith(LogOwnership.OwnerSuffix, StringComparison.Ordinal) || base.FileExists(path);

        public override PathKind KindOf(string path)
            => path.EndsWith(LogOwnership.OwnerSuffix, StringComparison.Ordinal) ? PathKind.File : base.KindOf(path);

        public override string ReadAllText(string path)
            => path.EndsWith(LogOwnership.OwnerSuffix, StringComparison.Ordinal) ? Owner : base.ReadAllText(path);
    }

    /// <summary>A leg on this machine and one on the ssh host.</summary>
    private static HarnessConfig TwoLegs(HarnessFactory harness) => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Hosts = new HostsConfig { Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree } } },
        Legs =
        {
            ["native"] = HostDoubles.Leg(harness.Platform.PlatformKey, harness.Platform.Processor),
            ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = HostName },
        },
    };

    /// <summary>This machine as it is, and the ssh host as a Linux arm64 machine that answers.</summary>
    private static RecordingInspector SshAndLocal(HarnessFactory harness) => new(host => host.Kind == HostKind.Local
        ? new HostReport { Host = host, Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor }
        : new HostReport { Host = host, Os = "linux", Processor = "arm64", Session = Session(host) });

    private static HostSession Session(HostId host)
        => new(new HostConnection { Host = host, Address = "192.0.2.10" }, ".dotnet/tools/dssharness");

    /// <summary>
    /// Runs the command over <paramref name="config"/>, a leg on this machine passing, and returns
    /// each leg's verdict and detail as the ledger a script reads reports them. Nothing may run on
    /// another machine: a leg that reached one would be a leg this run should not have started.
    /// </summary>
    private static async Task<Dictionary<string, (string? Verdict, string? Detail)>> RunAsync(
        TempDirectory temp,
        HarnessFactory harness,
        HarnessConfig config,
        RecordingInspector inspector,
        LegRunRequest request,
        RunLock? runLock = null,
        ISyncService? sync = null,
        Action<PlacedLeg>? ran = null,
        IProcessRunner? keepAwake = null,
        DeveloperEnvironmentProvider? developerEnvironments = null,
        LegAdmission? admission = null)
    {
        var outcome = await OutcomeAsync(temp, harness, config, inspector, request, runLock, sync, ran, keepAwake, developerEnvironments: developerEnvironments, admission: admission);

        return Verdicts(outcome);
    }

    /// <summary>Each leg's verdict and detail in <paramref name="outcome"/>, as the ledger a script reads reports them.</summary>
    private static Dictionary<string, (string? Verdict, string? Detail)> Verdicts(CommandOutcome outcome)
    {
        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));

        return document.RootElement.GetProperty("legs").EnumerateArray().ToDictionary(
            leg => leg.GetProperty("leg").GetString()!,
            leg => (leg.GetProperty("verdict").GetString(), leg.GetProperty("detail").GetString()));
    }

    /// <summary>
    /// Runs the command over <paramref name="config"/>, a leg on this machine passing unless
    /// <paramref name="work"/> says otherwise, and returns how it ended. The tree is the repository's
    /// root unless <paramref name="tree"/> names a worktree of it.
    /// </summary>
    private static async Task<CommandOutcome> OutcomeAsync(
        TempDirectory temp,
        HarnessFactory harness,
        HarnessConfig config,
        RecordingInspector inspector,
        LegRunRequest request,
        RunLock? runLock = null,
        ISyncService? sync = null,
        Action<PlacedLeg>? ran = null,
        IProcessRunner? keepAwake = null,
        string? tree = null,
        Func<LegWork, LegEntry>? work = null,
        LogOwnership? logs = null,
        ScriptedHostCommands? hosts = null,
        DeveloperEnvironmentProvider? developerEnvironments = null,
        LegAdmission? admission = null,
        ISyncTransportFactory? transports = null,
        Func<LegWork, CancellationToken, Task<LegEntry>>? workAsync = null,
        CommandOrigin? origin = null,
        IFileSystem? fileSystem = null,
        IWslPageCache? pageCache = null)
    {
        var loader = HostDoubles.Loader(config, tree ?? temp.Path, temp.Path);

        var service = new LegRunService(
            loader,
            new LegsService(loader, inspector, new KnownTrees(), harness.Platform, harness.Output),
            new LegExecutor(harness.Platform, harness.Output),
            runLock ?? new RunLock(harness.FileSystem, harness.Output, harness.Identity),
            logs ?? new LogOwnership(harness.FileSystem, harness.Output, harness.Identity),
            sync ?? Substitute.For<ISyncService>(),
            transports ?? Substitute.For<ISyncTransportFactory>(),
            new RemoteLegRunner(hosts ?? new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command)), harness.Output),

            // Never this machine's own record of its heavy legs: a test's slots are its own.
            admission ?? AdmissionKit.Admission(harness, AdmissionRecord(temp), new ScriptedGauge(10), new ManualClock()),
            pageCache ?? new ScriptedPageCache(),
            new KeepAwake(keepAwake ?? new HeldProcesses(), harness.Output),
            developerEnvironments ?? NoDeveloperEnvironment(harness),
            fileSystem ?? harness.FileSystem,
            harness.FilePermissions,
            harness.Platform,
            harness.Output,
            origin);

        return await service.RunAsync(
            "test",
            RunId.New(),
            request,
            (leg, token) =>
            {
                ran?.Invoke(leg.Leg);
                return workAsync?.Invoke(leg, token)
                    ?? Task.FromResult(work?.Invoke(leg) ?? new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed });
            },
            TestContext.Current.CancellationToken);
    }
}
