using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.SetUps;
using DssHarness.Core.Sync;
using DssHarness.Core.Tools;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

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
        var sync = SyncKit.ServiceDouble();
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
    /// A leg of a command whose legs on a host work in a copy of their own there - a sweep - is synced from this run's
    /// reading into that copy, kept in the .legs of the host's copy, and works there: a run building in the host's copy,
    /// of its own variant even, holds none of it off, and the host's copy is made where it is not there yet.
    /// </summary>
    [Fact]
    public async Task ALegThatWorksInACopyOfItsOwnOnAHost_IsNotHeldOffByARunInTheHostsCopy()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = SyncKit.ServiceDouble();
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var config = TwoLegs(harness);
        var variant = VariantKey.For(config, config.Legs["arm"], "linux");
        var copy = LegCopies.PathOf(HostTree, variant);

        await using var held = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            PlacedLeg.BuildLock(HostId.Ssh(HostName), HostTree, variant, RunId.New(), "test"),
            TestContext.Current.CancellationToken);

        HostCommand? sent = null;
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            sent = command;
            ScriptedHostCommands.Answer(command, """{"legs": [{"leg": "arm", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });

        var verdicts = Verdicts(await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["arm"], Json: true) { Workload = LegWorkload.Copy with { CopyOnHosts = true } },
            runLock,
            sync,
            hosts: hosts));

        Assert.True(verdicts["arm"].Verdict == "passed", $"{verdicts["arm"].Verdict}: {verdicts["arm"].Detail}");
        Assert.StartsWith(HostTree + "/.legs/", copy, StringComparison.Ordinal);
        Assert.Equal(copy, JsonSerializer.Deserialize<HostAgentRequest>(sent!.StandardInput.Read(), HostAgentProtocol.JsonOptions)!.Directory);
        await sync.Received(1).SyncAsync(
            Arg.Any<SyncSource>(),
            Arg.Any<ISyncTransport>(),
            copy,
            Arg.Is<SyncOptions>(options => options.KeptIn == HostTree && !options.Adoptable),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The host's copy of the tree that legs keep their own copies in has the tree synced to it first where it is not there
    /// yet as a copy this tool finished - not there, part-made, or a directory this tool did not make, which the sync
    /// refuses as any sync does - once a run, however many legs keep theirs in it, so every command working in the
    /// host's copy finds the tree there; each leg's own copy is synced as kept in it.
    /// </summary>
    [Theory]
    [InlineData(false, CopyMark.None, "is not there yet")]
    [InlineData(true, CopyMark.Unfinished, "is a copy whose last sync did not finish")]
    [InlineData(true, CopyMark.None, null)]
    public async Task TheHostsCopyLegsKeepTheirCopiesIn_IsSyncedFirst_OnceARun_WhereItIsNotThereYet(bool there, CopyMark mark, string? said)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = SyncKit.ServiceDouble();
        var config = TwoLegs(harness);

        config.BuildConfigs["release"] = new BuildConfiguration();
        config.Legs["arm-release"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "release", Ssh = HostName };

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            var arguments = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput.Read(), HostAgentProtocol.JsonOptions)!.Arguments;
            var leg = arguments.Any(argument => argument.Text == "arm-release") ? "arm-release" : "arm";

            ScriptedHostCommands.Answer(command, $$"""{"legs": [{"leg": "{{leg}}", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });

        // The host's copy found not there by every leg that asks, as legs asking at once each would find it: synced once all
        // the same. Each leg's own copy holds this run's reading once synced.
        var copy = Substitute.For<ISyncTransport>();
        var transports = Substitute.For<ISyncTransportFactory>();
        var run = RunId.New();

        copy.InspectAsync(Arg.Any<string>(), Arg.Any<GitHistoryWanted?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0) == HostTree
                ? new SyncInspectAnswer(there, mark)
                : new SyncInspectAnswer(true, CopyMark.Complete) { Reading = new CopyReading(run.Value, null, 0) });
        transports.For(Arg.Any<HostReport>()).Returns(copy);

        var verdicts = Verdicts(await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["arm", "arm-release"], Json: true) { Workload = LegWorkload.Copy with { CopyOnHosts = true } },
            sync: sync,
            hosts: hosts,
            transports: transports,
            runId: run));

        Assert.All(verdicts.Values, verdict => Assert.True(verdict.Verdict == "passed", $"{verdict.Verdict}: {verdict.Detail}"));
        // Said only where the sync goes on to write: one it refuses says why itself.
        var output = harness.StandardOutput.ToString() + harness.StandardError.ToString();

        if (said is null)
        {
            Assert.DoesNotContain("so the tree is synced to it first", output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"'{HostTree}' {said}, so the tree is synced to it first", output, StringComparison.Ordinal);
        }
        await sync.Received(1).SyncAsync(
            Arg.Any<SyncSource>(),
            Arg.Any<ISyncTransport>(),
            HostTree,
            Arg.Is<SyncOptions>(options => options.KeptIn == null && options.Adoptable),
            Arg.Any<CancellationToken>());
        await sync.Received(2).SyncAsync(
            Arg.Any<SyncSource>(),
            Arg.Any<ISyncTransport>(),
            Arg.Is<string>(root => root.StartsWith(HostTree + "/.legs/", StringComparison.Ordinal)),
            Arg.Is<SyncOptions>(options => options.KeptIn == HostTree),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The host's copy legs keep their own copies in, not there yet while another run holds it - syncing it - refuses
    /// those legs, naming that run, and nothing is synced under it.
    /// </summary>
    [Fact]
    public async Task TheHostsCopyLegsKeepTheirCopiesIn_NotThereYet_WhileAnotherRunHoldsIt_RefusesThoseLegs()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = SyncKit.ServiceDouble();
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);

        await using var held = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            new LockRequest
            {
                Host = HostId.Ssh(HostName).ToString(),
                Tree = HostTree,
                Scope = LockScope.TreeExclusive,
                RunId = RunId.New(),
                Command = SyncService.CommandName,
            },
            TestContext.Current.CancellationToken);

        var verdicts = await RunAsync(
            temp,
            harness,
            TwoLegs(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy with { CopyOnHosts = true } },
            runLock,
            sync,
            transports: SyncKit.CopiesSyncedBy(sync));

        Assert.Equal("passed", verdicts["native"].Verdict);
        Assert.Equal("refused-locked", verdicts["arm"].Verdict);
        Assert.Contains(held.Entry.Holder.RunId, verdicts["arm"].Detail, StringComparison.Ordinal);
        await sync.DidNotReceiveWithAnyArgs().SyncAsync(default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The host's copy legs keep their own copies in, not there yet while another run holds it, is taken with
    /// --force-lock, as any lock of a tree is, and synced.
    /// </summary>
    [Fact]
    public async Task TheHostsCopyLegsKeepTheirCopiesIn_NotThereYet_WhileAnotherRunHoldsIt_IsTakenWithForceLock()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = SyncKit.ServiceDouble();
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);

        await using var held = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            new LockRequest
            {
                Host = HostId.Ssh(HostName).ToString(),
                Tree = HostTree,
                Scope = LockScope.TreeExclusive,
                RunId = RunId.New(),
                Command = SyncService.CommandName,
            },
            TestContext.Current.CancellationToken);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            ScriptedHostCommands.Answer(command, """{"legs": [{"leg": "arm", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");
            return HostResults.Finished(command, 0);
        });

        var verdicts = Verdicts(await OutcomeAsync(
            temp,
            harness,
            TwoLegs(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["arm"], Json: true, ForceLock: true) { Workload = LegWorkload.Copy with { CopyOnHosts = true } },
            runLock,
            sync,
            hosts: hosts,
            transports: SyncKit.CopiesSyncedBy(sync)));

        Assert.True(verdicts["arm"].Verdict == "passed", $"{verdicts["arm"].Verdict}: {verdicts["arm"].Detail}");
        await sync.Received(1).SyncAsync(
            Arg.Any<SyncSource>(),
            Arg.Any<ISyncTransport>(),
            HostTree,
            Arg.Is<SyncOptions>(options => options.KeptIn == null && options.Adoptable),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A sync of the host's copy legs keep their own copies in that fails ends those legs with its failure, and no leg's
    /// own copy is synced into a copy that never became one.
    /// </summary>
    [Fact]
    public async Task TheHostsCopyLegsKeepTheirCopiesIn_ThatCannotBeSynced_EndsThoseLegs_NoCopyOfTheirsMade()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = SyncKit.ServiceDouble();

        sync.SyncAsync(Arg.Any<SyncSource>(), Arg.Any<ISyncTransport>(), HostTree, Arg.Any<SyncOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HarnessException(HarnessExit.CommandFailed, "the host's disk is full"));

        var verdicts = await RunAsync(
            temp,
            harness,
            TwoLegs(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy with { CopyOnHosts = true } },
            sync: sync,
            transports: SyncKit.CopiesSyncedBy(sync));

        Assert.Equal("passed", verdicts["native"].Verdict);
        Assert.NotEqual("passed", verdicts["arm"].Verdict);
        Assert.Contains("the host's disk is full", verdicts["arm"].Detail, StringComparison.Ordinal);
        await sync.DidNotReceive().SyncAsync(
            Arg.Any<SyncSource>(),
            Arg.Any<ISyncTransport>(),
            Arg.Is<string>(root => root.StartsWith(HostTree + "/.legs/", StringComparison.Ordinal)),
            Arg.Any<SyncOptions>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A second run of a variant whose leg works in a copy of its own on a host, while the first holds that copy - a
    /// sweep still running - is refused-locked before its sync rewrites the copy, naming the run holding it.
    /// </summary>
    [Fact]
    public async Task ACopyOfItsOwnOnAHostThatARunHolds_RefusesTheLeg_BeforeItsSyncRewritesIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = SyncKit.ServiceDouble();
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var config = TwoLegs(harness);
        var copy = LegCopies.PathOf(HostTree, VariantKey.For(config, config.Legs["arm"], "linux"));

        await using var held = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            LegCopies.Whole(copy, MutationService.CommandName, HostId.Ssh(HostName)),
            TestContext.Current.CancellationToken);

        var verdicts = await RunAsync(temp, harness, config, SshAndLocal(harness), new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy with { CopyOnHosts = true } }, runLock, sync);

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
        var sync = SyncKit.ServiceDouble();

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
    /// The reading a run's copies on other machines are made from is let go with the run, and with it what was packed
    /// of the tree's history for them: a pack is the size of what it carries, and nothing else removes it.
    /// </summary>
    [Fact]
    public async Task TheReadingARunsCopiesAreMadeFrom_IsLetGoWithTheRun()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = Substitute.For<ISyncService>();
        var reading = MutationFixture.Reading(temp.Path);

        sync.ReadSourceAsync(default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(reading);

        await RunAsync(temp, harness, TwoLegs(harness), SshAndLocal(harness), new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy }, sync: sync);

        await sync.ReceivedWithAnyArgs(1).ReadSourceAsync(default!, TestContext.Current.CancellationToken);
        Assert.True(reading.Disposed);
    }

    /// <summary>
    /// A tree that could not be read for its hosts' copies as the run began gives the legs on it their verdict, and only
    /// them: the leg on this machine still runs, and the run still names where its records are. Read ahead of the
    /// legs, outside them, its failure ended the whole run, with no ledger and no records named.
    /// </summary>
    [Theory]
    [InlineData(HarnessExit.CommandFailed, "failed", "git could not list what the tree ignores")]
    [InlineData(LegExit.InputsMoved, "inputs-moved", "'/src/repo' changed while it was read: 'src/a.c' was written. Let it settle, then run again.")]
    public async Task ATreeThatCouldNotBeRead_GivesItsOwnLegsTheirVerdict_AndTheOtherLegsStillRun(int exit, string verdict, string why)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = Substitute.For<ISyncService>();
        var ran = new List<string>();

        sync.ReadSourceAsync(default!, TestContext.Current.CancellationToken)
            .ThrowsAsyncForAnyArgs(new HarnessException(exit, why));

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
        Assert.Equal(verdict, legs["arm"].Verdict);
        Assert.Equal(why, legs["arm"].Detail);
        Assert.Equal(["native"], ran);
        Assert.NotEmpty(RunDirectoryOf(outcome, json: true));
    }

    /// <summary>
    /// A run on what is staged reads, on the machine that would have synced each copy, whether a sync or a takeover began
    /// there and did not finish: where one did, that copy holds part of one tree and part of another, so its legs are
    /// inputs-moved, naming it and what to do, and its host is never asked to test it; the legs elsewhere run. A copy that
    /// is not there, or that no sync of this tool marked, holds nothing current either. A host asked to run on what it
    /// holds was never told by the dispatcher that what it holds is staged, so it could not refuse.
    /// </summary>
    [Theory]
    [InlineData(CopyMark.Unfinished, true, "is a copy whose sync began and did not finish", "Run without --use-staged, which syncs it first.")]
    [InlineData(CopyMark.AdoptionStopped, true, "was being taken over and the run stopped before it finished", "sync --adopt \"ssh pi\"', and run again.")]
    [InlineData(CopyMark.Complete, true, null, null)]
    [InlineData(CopyMark.None, true, "holds no copy this tool made", "Run without --use-staged, which syncs it first.")]
    [InlineData(CopyMark.Complete, false, "holds no copy this tool made", "Run without --use-staged, which syncs it first.")]
    public async Task ARunOnWhatIsStaged_MakesInputsMovedTheLegsOfACopyLeftPartMade(CopyMark mark, bool exists, string? partMade, string? remedy)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var asked = 0;

        var copy = Substitute.For<ISyncTransport>();
        copy.InspectAsync(Arg.Any<string>(), Arg.Any<GitHistoryWanted?>(), Arg.Any<CancellationToken>())
            .Returns(new SyncInspectAnswer(exists, mark) { Reading = mark == CopyMark.Complete ? Staged : null });

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
        await copy.Received(1).InspectAsync(HostTree, Arg.Any<GitHistoryWanted?>(), Arg.Any<CancellationToken>());

        if (partMade is null)
        {
            Assert.Equal("passed", legs["arm"].Verdict);
            Assert.Contains(
                "test: arm: passed (tree staged at 0123456789ab with 2 uncommitted change(s) by run 20260101-000000-0a1b2c3d)",
                harness.StandardError.ToString() + harness.StandardOutput.ToString(),
                StringComparison.Ordinal);
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
    /// A leg that takes its host's copy once its run has synced it, and finds there the reading of another command's
    /// sync made in between, tests nothing there: it is inputs-moved, naming the copy and the run that synced it again -
    /// or none, for a sync typed by hand, which keeps no run - and its host is never asked to run it. A copy that holds
    /// this run's reading runs, and the leg's line, its detail and its tree say which reading it tested.
    /// </summary>
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "20260101-000000-0a1b2c3d")]
    [InlineData(false, null)]
    public async Task ACopyAnotherRunSyncedAfterThisRunsSync_EndsThisRunsLegInputsMoved_NamingThatRun(bool ours, string? another)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var run = RunId.New();
        var asked = 0;

        var copy = Substitute.For<ISyncTransport>();
        copy.InspectAsync(Arg.Any<string>(), Arg.Any<GitHistoryWanted?>(), Arg.Any<CancellationToken>())
            .Returns(new SyncInspectAnswer(true, CopyMark.Complete) { Reading = new CopyReading(ours ? run.Value : another, "0123456789abcdef", 0) });

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
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy },
            hosts: hosts,
            transports: transports,
            runId: run);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var arm = document.RootElement.GetProperty("legs").EnumerateArray().Single(leg => leg.GetProperty("leg").GetString() == "arm");
        var tree = arm.GetProperty("tree");

        Assert.Equal(HostTree, tree.GetProperty("path").GetString());
        Assert.Equal(temp.Path, tree.GetProperty("copyOf").GetString());

        if (ours)
        {
            Assert.Equal("passed", arm.GetProperty("verdict").GetString());
            Assert.Equal(string.Empty, arm.GetProperty("detail").GetString());
            Assert.Contains("test: arm: passed (tree read at 0123456789ab)", harness.StandardError.ToString() + harness.StandardOutput.ToString(), StringComparison.Ordinal);
            Assert.Equal(run.Value, tree.GetProperty("readBy").GetString());
            Assert.Equal("0123456789abcdef", tree.GetProperty("commit").GetString());
            Assert.Equal(0, tree.GetProperty("uncommittedChanges").GetInt32());
            Assert.Equal(1, asked);
        }
        else
        {
            Assert.Equal("inputs-moved", arm.GetProperty("verdict").GetString());
            Assert.Equal(
                $"'{HostTree}' on ssh {HostName} was synced again{(another is null ? string.Empty : $" by run {another}")} after this run "
                + "made it the tree it read, so it no longer holds that tree.",
                arm.GetProperty("detail").GetString());
            Assert.False(tree.TryGetProperty("readBy", out _));
            Assert.Equal(0, asked);
        }

        var native = document.RootElement.GetProperty("legs").EnumerateArray().Single(leg => leg.GetProperty("leg").GetString() == "native");

        Assert.Equal(temp.Path, native.GetProperty("tree").GetProperty("path").GetString());
        Assert.False(native.GetProperty("tree").TryGetProperty("copyOf", out _));
        Assert.False(native.GetProperty("tree").TryGetProperty("commit", out _));
    }

    /// <summary>
    /// A leg that takes its host's copy once its run has synced it, and finds it gone, left part made by a sync or a
    /// takeover that began and did not finish, or marked by no sync of this tool, tests nothing there: it is inputs-moved,
    /// naming the copy and which, and its host is never asked to run it - never a build in a directory that is not there,
    /// said as failed. The legs elsewhere run.
    /// </summary>
    [Theory]
    [InlineData(false, CopyMark.None, "it is not there, or no sync of this tool finished it.")]
    [InlineData(true, CopyMark.None, "it is not there, or no sync of this tool finished it.")]
    [InlineData(true, CopyMark.Unfinished, "it is a copy whose sync began and did not finish, so it holds part of one tree and part of another.")]
    [InlineData(
        true,
        CopyMark.AdoptionStopped,
        "it was being taken over and the run stopped before it finished, so it is neither the checkout it was nor a copy of this tree.")]
    public async Task ACopyGoneOrLeftPartMadeAfterThisRunsSync_EndsThisRunsLegInputsMoved_SayingWhich(bool exists, CopyMark mark, string why)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var asked = 0;

        var copy = Substitute.For<ISyncTransport>();
        copy.InspectAsync(Arg.Any<string>(), Arg.Any<GitHistoryWanted?>(), Arg.Any<CancellationToken>())
            .Returns(new SyncInspectAnswer(exists, mark));

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
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy },
            hosts: hosts,
            transports: transports);

        var legs = Verdicts(outcome);

        Assert.Equal("passed", legs["native"].Verdict);
        Assert.Equal("inputs-moved", legs["arm"].Verdict);
        Assert.Equal($"'{HostTree}' on ssh {HostName} no longer holds the tree this run read into it: {why}", legs["arm"].Detail);
        Assert.Equal(LegExit.InputsMoved, outcome.ExitCode);
        Assert.Equal(0, asked);
    }

    /// <summary>
    /// A run reads the tree its copies are made of once, says so without naming other machines, and then says the commit
    /// it was read at and how many of the paths it carries git calls changed - a path a sync withholds, never - or that it
    /// has no commit yet, on one line per tree.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ARunsReadingOfATree_SaysTheCommitItWasReadAt_AndHowManyOfItsPathsChanged(int changes)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        var copy = SyncKit.CopyPath(temp);
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig { Ssh = { [HostName] = new SshHostConfig { RepositoryPath = copy } } },
            Legs = { ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = HostName } },
        };

        config.Sync.Exclude.Add("notes/draft.txt");

        await harness.InitializeHarnessAsync(temp.Path, token, config);
        await harness.CommitAllAsync(temp.Path, "initial", token);

        // Changed, and carried to no copy.
        temp.WriteFile("notes/draft.txt", "kept here\n");

        for (var change = 0; change < changes; change++)
        {
            temp.WriteFile($"src/new{change}.c", "int x;\n");
        }

        var transports = Substitute.For<ISyncTransportFactory>();
        transports.For(Arg.Any<HostReport>()).Returns(call => new RecordingTransport(SyncKit.Transport(harness), reports: call.Arg<HostReport>().Host));

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            ScriptedHostCommands.Answer(command, """{"legs": [{"leg": "arm", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

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

            var commit = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", token))![..12];
            var uncommitted = changes > 0 ? $" with {changes} uncommitted change(s)" : string.Empty;
            var lines = (harness.StandardOutput.ToString() + harness.StandardError.ToString()).Split('\n').Select(line => line.TrimEnd('\r')).ToList();

            Assert.Equal("passed", Verdicts(outcome)["arm"].Verdict);
            Assert.Contains($"test: reading '{temp.Path}' for its copies", lines);
            Assert.Contains($"test: '{temp.Path}' was read at commit {commit}{uncommitted}", lines);
            Assert.Contains($"test: arm: passed (tree read at {commit}{uncommitted})", lines);
            Assert.DoesNotContain(lines, line => line.Contains("on other machines", StringComparison.Ordinal));
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
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
        var sync = SyncKit.ServiceDouble();

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
            hosts: hosts,
            transports: SyncKit.CopiesHolding(Staged));

        var legs = Verdicts(outcome);

        Assert.Equal("passed", legs["native"].Verdict);
        Assert.Equal("passed", legs["arm"].Verdict);
        await sync.DidNotReceiveWithAnyArgs().ReadSourceAsync(default!, TestContext.Current.CancellationToken);
        await sync.DidNotReceiveWithAnyArgs().SyncAsync(default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A leg placed on a host is sent under the run that placed it, beside its command: the legs of one command there ask
    /// the host's heavy-leg slots under one run, and wait for each other's without that wait counting.
    /// </summary>
    [Fact]
    public async Task ALegPlacedOnAHost_IsSentUnderTheRunThatPlacedIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var run = RunId.New();
        HostCommand? sent = null;

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            sent = command;
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
            runId: run);

        Assert.Equal("passed", Verdicts(outcome)["arm"].Verdict);
        Assert.NotNull(sent);
        Assert.Equal(run.Value, JsonSerializer.Deserialize<HostAgentRequest>(sent.StandardInput.Read(), HostAgentProtocol.JsonOptions)!.RunId);
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

        // A lock file nobody can read is no run to wait for: its 13 tells no caller to try again.
        Assert.Equal(OutcomeCause.None, outcome.Cause);

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
                ["dh-probe"] = new("dh-probe", host.Kind == HostKind.Local ? ProgramFound.Nowhere : ProgramFound.OnPath, "/usr/bin/dh-probe"),
            },
            Session = host.Kind == HostKind.Local ? null : Session(host),
        });

        var verdicts = await RunAsync(
            temp,
            harness,
            config,
            inspector,
            new LegRunRequest(temp.Path, null, Json: true, UseStaged: useStaged) { Workload = new LegWorkload(Build: false, Test: false, ["dh-probe"]) });

        Assert.Equal("skipped-tool-missing", verdicts["here"].Verdict);
        Assert.StartsWith("local: 'dh-probe' is not installed there", verdicts["here"].Detail, StringComparison.Ordinal);
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
    /// A run tells a front end its directory as it is made - what it is, and its own path - so one following the run
    /// reads its records where it is handed them and puts no path together; and that is the one record it is told of.
    /// </summary>
    [Fact]
    public async Task ARun_TellsAFrontEndItsDirectory_AsItIsMade()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sink = new RecordingSink();
        var run = RunId.New();
        CommandOutcome outcome;

        using (var output = new EventHarnessOutput(sink, OperationId.New()))
        {
            outcome = await OutcomeAsync(
                temp,
                harness,
                OneLeg(harness),
                SshAndLocal(harness),
                new LegRunRequest(temp.Path, null) { Workload = LegWorkload.Copy },
                runId: run,
                output: output);
        }

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(
            [new RecordWritten("run directory", new HarnessLayout(temp.Path, temp.Path).RunDirectory(run.Value))],
            sink.Seen.OfType<RecordWritten>().Select(@event => @event with { Operation = null, Sequence = 0, At = default }));
    }

    /// <summary>
    /// A leg's work here is given the repository's other trees on this machine - every tree but its own, the main
    /// checkout among them for a worktree's leg - by which whose build directory a process found beside it works in is
    /// told; and why they could not be listed, where they could not. Its own is told however the listing spells it - here
    /// with a separator after it - as this platform compares paths: given as another tree, its own build directory would
    /// be another tree's leg's.
    /// </summary>
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, "git worktree list exited 128", false)]
    public async Task ALegsWorkHere_IsGivenTheRepositorysOtherTreesOnThisMachine(bool inWorktree, string? unlisted, bool spelledOtherwise)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var feature = new RepositoryTree(temp.Combine("feature"), "worktree feature");
        var other = new RepositoryTree(temp.Combine("other"), "worktree other");
        var tree = inWorktree ? feature.Root : temp.Path;
        var listedFeature = spelledOtherwise ? feature with { Root = feature.Root + Path.DirectorySeparatorChar } : feature;
        RepositoryTreesFound? beside = null;
        var config = OneLeg(harness);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(tree, null) { Workload = LegWorkload.Copy },
            ran: leg => beside = leg.BuildRequestFor(config, temp.Path, new ProjectConfig { Name = "app", Type = "cmake" }).Beside,
            tree: tree,
            trees: new KnownTrees { Beside = _ => [listedFeature, other], Unlisted = unlisted });

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.NotNull(beside);
        Assert.Equal(inWorktree ? [new RepositoryTree(temp.Path, RepositoryTree.MainCheckout), other] : [feature, other], beside.Trees);
        Assert.Equal(unlisted, beside.Unlisted);
    }

    /// <summary>
    /// The repository's other trees here are listed as each leg's work begins, never once for the run: a worktree made
    /// while the run's first leg worked - an agent's, made by its orchestrator - is known to every leg that begins after.
    /// </summary>
    [Fact]
    public async Task TheOtherTreesHere_AreListedAsEachLegsWorkBegins()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var feature = new RepositoryTree(temp.Combine("feature"), "worktree feature");
        var listed = new List<RepositoryTree> { feature };
        var besides = new List<string>();
        var config = new HarnessConfig
        {
            Defaults = new HarnessDefaults { MaxParallelLegs = 1 },
            BuildConfigs = { ["debug"] = new BuildConfiguration(), ["release"] = new BuildConfiguration() },
            Legs =
            {
                ["native"] = HostDoubles.Leg(harness.Platform.PlatformKey, harness.Platform.Processor),
                ["native-release"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "release" },
            },
        };

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null) { Workload = LegWorkload.Copy },
            ran: leg =>
            {
                besides.Add(string.Join(", ", leg.Beside!.Trees.Select(tree => tree.Name)));
                listed.Add(new RepositoryTree(temp.Combine("o1", "xa"), "worktree o1/xa"));
            },
            trees: new KnownTrees { Beside = _ => [.. listed.Distinct()] });

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(["worktree feature", "worktree feature, worktree o1/xa"], besides);
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

        // A run whose logs another owns is pointed elsewhere, never waited out as a lock is, and a refusal of the leg's
        // configuration is put right: neither tells a caller to try again.
        Assert.Equal(OutcomeCause.None, outcome.Cause);
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
    /// A leg another host ran was run there under a run of its own: its own directory of records and the logs in it are
    /// named beside the run's - as the host spelled them - with the run the host named as it began, in the text and on
    /// the leg's JSON line.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALegAnotherHostRan_NamesItsOwnRecordsAndTheRunTheHostRanItUnder(bool json)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        const string There = HostTree + "/.harness-config/runs/20260919-101500-0a1b2c3d";
        const string Records = There + "/arm";

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            ScriptedHostCommands.Answer(
                command,
                $$"""{"runDirectory": "{{There}}", "runId": "20260919-101500-0a1b2c3d", "legs": [{"leg": "arm", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1, "records": "{{Records}}", "logs": [{"phase": "build", "file": "{{Records}}/build.log"}, {"phase": "test", "file": "{{Records}}/test.log"}]}]}""");

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
            Assert.Equal("20260919-101500-0a1b2c3d", arm.GetProperty("hostRunId").GetString());
            Assert.Equal(Records, arm.GetProperty("records").GetString());
            Assert.Equal(
                [("build", Records + "/build.log"), ("test", Records + "/test.log")],
                arm.GetProperty("logs").EnumerateArray().Select(log => (log.GetProperty("phase").GetString(), log.GetProperty("file").GetString())));
        }
        else
        {
            // The closing block names the leg's records, and the line said as its verdict is names them the same way,
            // with the host that ran the leg before them.
            Assert.Contains($"logs of arm on ssh {HostName}: {Records} (build.log, test.log)", outcome.Details ?? [], StringComparer.Ordinal);
            Assert.Contains(
                $"test: arm: logs of arm on ssh {HostName}: {Records} (build.log, test.log)",
                (harness.StandardOutput.ToString() + harness.StandardError.ToString()).Split('\n').Select(line => line.TrimEnd('\r')),
                StringComparer.Ordinal);
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
    /// A stop another process asks of a run (stop-run) is taken at the run's next look and stops it as an interrupt does:
    /// the leg at work stopped where it stands, the run ended interrupted, its line naming who asked - its claim given up,
    /// the request gone, and the stop recorded taken among the run's records for every process that asked.
    /// </summary>
    [Fact]
    public async Task AStopAskedOfARun_StopsItAsAnInterruptDoes_AndRecordsItTakenAmongItsRecords()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var stops = new RunStops(harness.FileSystem, harness.Identity, harness.Output, new LogOwnership(harness.FileSystem, harness.Output, harness.Identity));
        string? asked = null;

        var outcome = await OutcomeAsync(
            temp,
            harness,
            OneLeg(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy },
            stops: stops,
            workAsync: async (work, token) =>
            {
                stops.Ask(asked = work.RunDirectory);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);

                throw new InvalidOperationException("the leg's work outlived the stop asked of its run");
            });

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.True(document.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.Contains(
            $"test: WARN - pid {Environment.ProcessId} asked this run to stop ({RunStopService.CommandName}): its legs are stopped where they stand, as an interrupt stops them.",
            harness.StandardError.ToString(),
            StringComparison.Ordinal);
        Assert.False(File.Exists(RunStops.FileOf(asked!)), "the request the run took stayed asked");
        Assert.True(stops.TakenBack(asked!, "test"), "the run's records did not say it took the stop");
        Assert.False(File.Exists(LogOwnership.OwnerFile(asked!)), "the run's claim was left behind it");
    }

    /// <summary>
    /// A run's watch for a stop ends before its directory is given up: a stop asked as the run gives its directory up is
    /// never taken by the run that ended, and is left, untaken, for the process that asked to take back - never removed by
    /// a watch that ended after the directory was given up.
    /// </summary>
    [Fact]
    public async Task AStopAskedAsARunGivesItsDirectoryUp_IsNeverTakenByIt_AndIsLeftForItsAsker()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var stops = new RunStops(harness.FileSystem, harness.Identity, harness.Output, new LogOwnership(harness.FileSystem, harness.Output, harness.Identity));
        string? runDirectory = null;
        var givingUp = new AskingAsTheOwnerGoes(harness.FileSystem, path => runDirectory is { } run && path == LogOwnership.OwnerFile(run), () => stops.Ask(runDirectory!));

        var outcome = await OutcomeAsync(
            temp,
            harness,
            OneLeg(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null) { Workload = LegWorkload.Copy },
            logs: new LogOwnership(givingUp, harness.Output, harness.Identity),
            stops: stops,
            work: leg =>
            {
                runDirectory = leg.RunDirectory;
                return new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed };
            });

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.True(File.Exists(RunStops.FileOf(runDirectory!)), "a stop asked as the run gave its directory up was removed after the giving up");
        Assert.False(stops.TakenBack(runDirectory!, "test"), "a stop asked as the run gave its directory up was taken by the run that ended");
        Assert.False(File.Exists(RunStops.FileOf(runDirectory!)), "the process that asked left its untaken stop behind the run");
    }

    /// <summary>The file system, but that it runs <paramref name="then"/> just before a file <paramref name="owner"/> picks out is removed.</summary>
    private sealed class AskingAsTheOwnerGoes(IFileSystem inner, Func<string, bool> owner, Action then) : PassThroughFileSystem(inner)
    {
        public override void DeleteFile(string path)
        {
            if (owner(path))
            {
                then();
            }

            base.DeleteFile(path);
        }
    }

    /// <summary>
    /// A stop asked of a run that ended without taking it - killed before its next look - goes with the run's claim when
    /// the next run in its tree finds it abandoned: nothing would ever take it.
    /// </summary>
    [Fact]
    public async Task AStopAskedOfAnAbandonedRun_GoesWithItsClaim()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var dead = temp.Combine(".harness-config", "runs", "20250101-120000-deadbeef");

        Directory.CreateDirectory(dead);
        File.WriteAllText(
            LogOwnership.OwnerFile(dead),
            "{ \"machine\": \"" + Environment.MachineName + "\", \"processId\": " + (int.MaxValue - 1).ToString(CultureInfo.InvariantCulture)
            + ", \"processStamp\": \"a-process-that-has-gone\", \"runId\": \"20250101-120000-deadbeef\", \"takenUtc\": \"2025-01-01T12:00:00+00:00\" }");
        new RunStops(harness.FileSystem, harness.Identity, harness.Output, new LogOwnership(harness.FileSystem, harness.Output, harness.Identity)).Ask(dead);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            OneLeg(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null) { Workload = LegWorkload.Copy },
            stops: new RunStops(harness.FileSystem, harness.Identity, harness.Output, new LogOwnership(harness.FileSystem, harness.Output, harness.Identity)));

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.False(File.Exists(RunStops.FileOf(dead)), "the stop asked of the abandoned run was left behind it");
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
        DeveloperEnvironmentFact? handed = null;

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
                handed = leg.DeveloperEnvironment;
                return new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed };
            },
            developerEnvironments: visualStudio.Provider());

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);

        // The work is handed what was set up, as a sweep's key holds it.
        Assert.Equal(("vs", visualStudio.InstallationPath, ScriptedVisualStudio.ToolsVersion, "amd64"), (handed?.Name, handed?.InstallationPath, handed?.ToolsVersion, handed?.Architecture));
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
    /// The toolset the survey found for a developer environment that names one is the one the leg is set up in: vcvarsall.bat
    /// is asked for it, and the leg is handed it and names it.
    /// </summary>
    [Fact]
    public async Task ALegWhoseDeveloperEnvironmentNamesAToolset_IsSetUpInTheToolsetTheSurveyFound()
    {
        using var temp = new TempDirectory();
        using var visualStudio = new ScriptedVisualStudio().Carrying("x64", "cl", "cmake");
        var harness = new HarnessFactory();
        var pinned = DeveloperEnvironmentCheck.Installed(visualStudio.InstallationPath, ScriptedVisualStudio.InstallationVersion, "14.44.35207");
        DeveloperEnvironmentFact? handed = null;

        var outcome = await OutcomeAsync(
            temp,
            harness,
            MsvcLeg(),
            new RecordingInspector(host => new HostReport
            {
                Host = host,
                Os = "windows",
                Processor = "x86_64",
                DeveloperEnvironments = new Dictionary<string, DeveloperEnvironmentCheck>(StringComparer.OrdinalIgnoreCase) { ["vs"] = pinned },
            }),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly },
            work: leg =>
            {
                handed = leg.DeveloperEnvironment;
                return new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed };
            },
            developerEnvironments: visualStudio.Provider());

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(
            "amd64 -vcvars_ver=14.44.35207",
            Assert.Single(visualStudio.Captures).Environment[DeveloperEnvironmentProvider.ArgumentsVariable]);
        Assert.Equal("14.44.35207", handed?.ToolsVersion);
        Assert.Contains(
            $"native: passed (developer environment: vs (Visual Studio {ScriptedVisualStudio.InstallationVersion}, MSVC 14.44.35207, amd64))",
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

    /// <summary>
    /// A declared tool the PATH a developer environment set up names is held to its minimum version there, before
    /// anything of the leg starts: the copy Visual Studio carries is the one the leg starts, and one older than the
    /// configuration needs skips the leg as a tool missing, named with what it said, as the survey names one it can see.
    /// </summary>
    [Fact]
    public async Task AToolTheDeveloperEnvironmentCarriesBelowItsMinimum_SkipsItsLeg_NamingIt_BeforeAnythingOfItStarts()
    {
        using var temp = new TempDirectory();
        using var visualStudio = new ScriptedVisualStudio().Carrying("x64", "cl", "cmake");
        var harness = new HarnessFactory();
        var ran = new List<string>();
        var config = MsvcLeg(hostSetsPath: false);
        config.Tools.Add(new ToolConfig { Name = "cmake", Probe = new ToolProbe { Args = ["--version"], Regex = @"(\d+\.\d+\.\d+)" }, MinVersion = "3.28.0" });
        var probes = Substitute.For<IProcessRunner>();
        probes.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(HostResults.Ok("cmake version 3.25.1\n")));

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            WindowsHere(visualStudio),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly },
            ran: leg => ran.Add(leg.Name),
            developerEnvironments: visualStudio.Provider(),
            versionProbes: probes);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());
        var cmake = Assert.Single(probes.ReceivedCalls()).GetArguments()[0] as ProcessRequest;

        Assert.Equal("skipped-tool-missing", leg.GetProperty("verdict").GetString());
        Assert.Equal(
            $"no copy of 'cmake' there is at least 3.28.0, the minVersion it is declared with: {cmake!.FileName} is 3.25.1; put a copy of at "
            + "least that version where a leg there finds programs: on the PATH, or in a directory searched for programs",
            leg.GetProperty("detail").GetString());
        Assert.StartsWith(visualStudio.InstallationPath, cmake.FileName, StringComparison.OrdinalIgnoreCase);
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
            origin: CommandOrigin.Served(Dispatch.Of(new HostAgentRequest { Kind = HostAgentRequestKind.Run, RunId = "20261008-120000-0a1b2c3d" })));

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
    /// the memory, and its line says so: without waiting for what comes back, which the next leg's wait reads. Where WSL
    /// lists no distribution a host reaches running, though the leg had just run in one, its line says nothing was dropped.
    /// A leg of an ssh host, and one where this machine admits nothing, drop none.
    /// </summary>
    [Theory]
    [InlineData("wsl", true, true, "as the leg ended, WSL's page cache was dropped as root in wsl Ubuntu, 1 GiB of it")]
    [InlineData(
        "wsl",
        true,
        false,
        "as the leg ended, WSL's page cache was not dropped: WSL listed no distribution a WSL host reaches as running, though this leg had just run in one")]
    [InlineData("wsl", false, true, null)]
    [InlineData("ssh", true, true, null)]
    public async Task AWslLegsEnd_DropsWslsPageCache_WhereThisMachineAdmitsByTheMemory(string kind, bool admits, bool running, string? said)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cache = new ScriptedPageCache(running ? PageCacheDrop.Dropped(HostId.Wsl("Ubuntu"), AdmissionKit.Gibibyte) : null);

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

        var printed = harness.StandardOutput.ToString() + harness.StandardError.ToString();

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(said is null ? [] : [config], cache.Asked.Select(context => context.Config));
        Assert.Equal(
            said is null ? [] : [true],
            printed.Split('\n').Where(line => line.Contains("as the leg ended", StringComparison.Ordinal)).Select(line => line.Contains($"remote: {said}", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A stop asked for while a WSL leg's end drops WSL's page cache loses nothing of the leg: its verdict, in hand by then,
    /// is reported and recorded as any leg's is, and its line says the cache was not dropped, and why.
    /// </summary>
    [Fact]
    public async Task AStopWhileAWslLegsEndDropsTheCache_KeepsTheLegsVerdict()
    {
        using var temp = new TempDirectory();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var harness = new HarnessFactory();
        var cache = new ScriptedPageCache(PageCacheDrop.Dropped(HostId.Wsl("Ubuntu"), AdmissionKit.Gibibyte))
        {
            WhileDropping = token =>
            {
                stop.Cancel();
                token.ThrowIfCancellationRequested();
            },
        };

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig { Admission = new AdmissionSettings() },
                Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" } },
            },
            Legs = { ["remote"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Wsl = "Ubuntu" } },
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
            pageCache: cache,
            cancellationToken: stop.Token);
        var printed = harness.StandardOutput.ToString() + harness.StandardError.ToString();

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Contains("remote: as the leg ended, WSL's page cache was not dropped: the run was stopped first", printed, StringComparison.Ordinal);
        Assert.Equal("passed", Verdicts(outcome)["remote"].Verdict);
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
            ran: leg => (ran, held) = (true, leg.BuildRequestFor(config, temp.Path, new ProjectConfig { Name = "app", Type = "cmake" }).Floor));

        Assert.True(ran);
        Assert.Equal(floor, held?.Bytes);
        Assert.Equal(floor is null ? null : "native", held?.Leg);
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
            origin: CommandOrigin.Served(Dispatch.Of(new HostAgentRequest { Kind = HostAgentRequestKind.Run, RunId = RunId.New().Value, DiskImageDrive = drive })),
            fileSystem: new MountsListing(harness.FileSystem, mounts));

        Assert.NotNull(held);
        Assert.Equal(2L << 30, held.Bytes);
        Assert.Equal(mount is null ? Array.Empty<(string, string)>() : [(mount, ", where WSL keeps its disk")], held.Also);
        Assert.Equal(unwatched, held.Unwatched);
    }

    /// <summary>The real file system, except that what is mounted reads as <paramref name="mounts"/> says.</summary>
    private sealed class MountsListing(IFileSystem inner, string mounts) : PassThroughFileSystem(inner)
    {
        public override Stream OpenRead(string path)
            => path == WindowsDriveMounts.MountsFile ? Reading(mounts) : base.OpenRead(path);
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
    /// A host sent a leg of such a command admits each unit by the admission its own entry among the hosts declares, and
    /// holds its builds to the room that entry keeps free: what the machine that typed the command declares for itself
    /// says nothing of this one's memory or disks, so a host whose entry declares none asks nothing and holds nothing,
    /// whatever that machine declares.
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
        long? floor = null;

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
                floor = work.Leg.Floor?.Bytes;

                return new LegEntry { Leg = work.Leg.Name, Verdict = LegVerdict.Passed };
            });

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal([declared, declared], taken);
        Assert.Equal(declared ? 2L << 30 : null, floor);
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

    /// <summary>What a test's staged copies hold: another run's reading, made before this one began.</summary>
    private static CopyReading Staged => new("20260101-000000-0a1b2c3d", "0123456789abcdef0123", 2);

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

        public override Stream OpenRead(string path)
            => path.EndsWith(LogOwnership.OwnerSuffix, StringComparison.Ordinal) ? Reading(Owner) : base.OpenRead(path);
    }

    /// <summary>
    /// A run asked to set up brings each set-up its leg's work names up before that work starts, in the order named, at
    /// the root of the tree the leg works in, and down after it in the reverse order; each line runs as a phase of the
    /// leg, its log among the leg's records, and the leg's line and document say what it did with each.
    /// </summary>
    [Fact]
    public async Task ARunAskedToSetUp_BringsEachUpBeforeTheWork_AndDownAfterIt_InReverse()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        var config = SetUpConfig(harness, ("a", SetUpScripts.SetUp("a")), ("b", SetUpScripts.SetUp("b")));
        var (setUps, holds) = SetUpsFor(harness, state);
        IReadOnlyList<string>? duringWork = null;
        IReadOnlyList<SetUpPhase>? heldDuringWork = null;

        SetUpScripts.Write(temp.Path);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload("a", "b") with { SetUp = true } },
            work: leg =>
            {
                duringWork = SetUpScripts.Logged(temp.Path);
                heldDuringWork = [.. holds.HeldOn("a", temp.Path).Concat(holds.HeldOn("b", temp.Path)).Select(hold => hold.Phase)];
                return new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed };
            },
            setUps: setUps);

        Assert.True(HarnessExit.Success == outcome.ExitCode, $"the run ended {outcome.ExitCode}: {outcome.Message}");
        Assert.Equal<string>(["up-a", "up-b"], duringWork!);

        // Up while the work runs, so another leg finding it joins rather than waits.
        Assert.Equal([SetUpPhase.Standing, SetUpPhase.Standing], heldDuringWork!);
        Assert.Equal<string>(["up-a", "up-b", "down-b", "down-a"], SetUpScripts.Logged(temp.Path));
        Assert.Equal(
            [("a", "brought-up", "brought-down"), ("b", "brought-up", "brought-down")],
            SetUpFacts(outcome));
        Assert.Contains("test: native: bringing set-up 'a' up", harness.StandardOutput.ToString() + harness.StandardError.ToString(), StringComparison.Ordinal);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(temp.Path, ".harness-config"), "set-up b down.*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// A set-up's up line is left running on purpose - what it brings up is shared, held across runs, and ended by its
    /// down line - so it is not bound to the process running the leg; its down line is, as every other phase is.
    /// </summary>
    [Fact]
    public async Task ASetUpsUpLine_IsLeftRunning_AndItsDownLineIsBound()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        var seen = new RequestsSeen(harness.ProcessRunner);

        SetUpScripts.Write(temp.Path);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a"))),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload("a") with { SetUp = true } },
            setUps: SetUpsFor(harness, state, phases: seen).SetUps);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal<string>(["up-a", "down-a"], SetUpScripts.Logged(temp.Path));
        Assert.Equal(
            [("up-a", false), ("down-a", true)],
            seen.Seen.Select(request => (request.Arguments.First(argument => argument.EndsWith("-a", StringComparison.Ordinal)), request.Bound)));
    }

    /// <summary>Naming set-ups changes no run that is not asked to set up: nothing is brought up or down.</summary>
    [Fact]
    public async Task ARunNotAskedToSetUp_BringsNothingUp()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();

        SetUpScripts.Write(temp.Path);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a"))),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload("a") },
            setUps: SetUpsFor(harness, state).SetUps);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Empty(SetUpScripts.Logged(temp.Path));
        Assert.Empty(SetUpFacts(outcome));
    }

    /// <summary>
    /// A set-up that does not come up fails the leg before its work starts, saying which and where its log is - and
    /// every set-up the leg brought up is brought down, the one that failed among them, in the reverse order. One named
    /// after it was held but never tried: it did nothing, and the leg says nothing of it.
    /// </summary>
    [Fact]
    public async Task ASetUpThatDoesNotComeUp_FailsTheLegBeforeItsWork_AndWhatCameUpIsBroughtDown()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();

        SetUpScripts.Write(temp.Path);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a")), ("b", SetUpScripts.SetUp("b", upExit: 5)), ("c", SetUpScripts.SetUp("c"))),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload("a", "b", "c") with { SetUp = true } },
            work: _ => throw new InvalidOperationException("the leg's work started after a set-up did not come up"),
            setUps: SetUpsFor(harness, state).SetUps);

        var leg = Verdicts(outcome)["native"];

        Assert.Equal(HarnessExit.CommandFailed, outcome.ExitCode);
        Assert.Equal("failed", leg.Verdict);
        Assert.StartsWith("set-up 'b' did not come up: 'scripts/mark.", leg.Detail, StringComparison.Ordinal);
        Assert.EndsWith(" up-b 5' exited 5; its log is 'set-up b up.log' among the leg's records", leg.Detail, StringComparison.Ordinal);
        Assert.Equal<string>(["up-a", "up-b", "down-b", "down-a"], SetUpScripts.Logged(temp.Path));
        Assert.Equal([("a", "brought-up", "brought-down"), ("b", "failed", "brought-down")], SetUpFacts(outcome));
    }

    /// <summary>
    /// A set-up that does not come down leaves the run incomplete, 21, though the leg passed: what it started may still
    /// run, and the run's last line says which leg left it and how to bring it down.
    /// </summary>
    [Fact]
    public async Task ASetUpThatDoesNotComeDown_LeavesTheRunIncomplete_SayingHowToBringItDown()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();

        SetUpScripts.Write(temp.Path);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a", downExit: 6))),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload("a") with { SetUp = true } },
            setUps: SetUpsFor(harness, state).SetUps);

        Assert.Equal(HarnessExit.Incomplete, outcome.ExitCode);
        Assert.Equal("passed", Verdicts(outcome)["native"].Verdict);
        Assert.Contains("native did not bring set-up 'a' down; 'dssharness down a', run in the tree the leg worked in, brings it down", outcome.Message, StringComparison.Ordinal);
        Assert.Equal([("a", "brought-up", "failed")], SetUpFacts(outcome));
    }

    /// <summary>
    /// A run interrupted while its leg works still brings down what the leg brought up, given as long as an interrupted
    /// deletion is - a down that takes a while is let finish: a set-up left up runs on after every run that used it.
    /// </summary>
    [Fact]
    public async Task AnInterruptedRun_StillBringsDownWhatItsLegBroughtUp()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        using var stop = new CancellationTokenSource();
        var harness = new HarnessFactory();

        SetUpScripts.Write(temp.Path);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a", downSeconds: 2))),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload("a") with { SetUp = true } },
            workAsync: async (leg, token) =>
            {
                await stop.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
                return new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed };
            },
            setUps: SetUpsFor(harness, state).SetUps,
            cancellationToken: stop.Token);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal<string>(["up-a", "down-a"], SetUpScripts.Logged(temp.Path));
        Assert.DoesNotContain("did not come down", harness.StandardOutput.ToString() + harness.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A run stopped while its leg waits for a set-up another leg is bringing up lets go of the one it had joined already,
    /// leaving it up for the leg that still uses it, and brings nothing up or down: it held the later one never.
    /// </summary>
    [Fact]
    public async Task ARunStoppedWhileItsLegWaitsForASetUp_LetsGoOfWhatItHeld_AndRunsNoLine()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        using var stop = new CancellationTokenSource();
        var harness = new HarnessFactory();
        var (setUps, holds) = SetUpsFor(harness, state, wait: async (_, token) =>
        {
            await stop.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
        });
        var token = TestContext.Current.CancellationToken;

        SetUpScripts.Write(temp.Path);
        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("a", new Dictionary<string, string>(), temp.Path, "another-run", "other", _ => { }, token));
        holds.Stood("a", temp.Path, "another-run", "other");
        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("b", new Dictionary<string, string>(), temp.Path, "another-run", "other", _ => { }, token));

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a")), ("b", SetUpScripts.SetUp("b"))),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload("a", "b") with { SetUp = true } },
            work: _ => throw new InvalidOperationException("the leg's work started while it waited for a set-up"),
            setUps: setUps,
            cancellationToken: stop.Token);

        IReadOnlyList<string> held = [.. holds.HeldOn("a", temp.Path).Select(hold => hold.Leg)];

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Empty(SetUpScripts.Logged(temp.Path));
        Assert.Equal<string>(["other"], held);
        Assert.Equal(SetUpPhase.Rising, Assert.Single(holds.HeldOn("b", temp.Path)).Phase);
    }

    /// <summary>
    /// A leg finding a set-up up for another leg in its tree uses it and leaves it up for that leg, which brings it down:
    /// it runs neither line.
    /// </summary>
    [Fact]
    public async Task ALegFindingASetUpUpForAnotherLeg_UsesIt_AndLeavesItForThatLeg()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        var (setUps, holds) = SetUpsFor(harness, state);

        SetUpScripts.Write(temp.Path);
        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("a", new Dictionary<string, string>(), temp.Path, "another-run", "other", _ => { }, TestContext.Current.CancellationToken));
        holds.Stood("a", temp.Path, "another-run", "other");

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a"))),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload("a") with { SetUp = true } },
            setUps: setUps);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Empty(SetUpScripts.Logged(temp.Path));
        Assert.Equal([("a", "joined", "left-for-other-legs")], SetUpFacts(outcome));
        Assert.Equal("other", Assert.Single(holds.HeldOn("a", temp.Path)).Leg);
    }

    /// <summary>
    /// A leg finding a set-up the machine shares up for a leg in another tree uses it, and leaves it up for that leg: legs in
    /// two trees never each bring up, and bring down under the other, what the machine holds once. The leg works in a
    /// worktree, whose repository is its main checkout's.
    /// </summary>
    [Fact]
    public async Task ALegFindingASetUpTheMachineSharesUpForALegInAnotherTree_UsesIt()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        var (setUps, holds) = SetUpsFor(harness, state);
        var elsewhere = Path.Combine(temp.Path, "..", "elsewhere-" + Guid.NewGuid().ToString("N")[..8]);
        var shared = SetUpScripts.SetUp("a");
        IReadOnlyList<string>? holding = null;
        var repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temp.Path));
        var worktree = Directory.CreateDirectory(Path.Combine(temp.Path, "worktree")).FullName;

        SetUpScripts.Write(worktree);
        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync(
            "a", new Dictionary<string, string>(), elsewhere, "another-run", "other", _ => { }, TestContext.Current.CancellationToken,
            new SetUpSharing(repository, MachineWide: true)));
        holds.Stood("a", elsewhere, "another-run", "other");

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", new Core.Configuration.SetUpConfig { Up = shared.Up, Down = shared.Down, Shared = Core.Configuration.SetUpConfig.SharedByMachine })),
            SshAndLocal(harness),
            new LegRunRequest(worktree, ["native"], Json: true) { Workload = SetUpWorkload("a") with { SetUp = true } },
            tree: worktree,
            work: leg =>
            {
                // Asked from the other tree as a leg of the repository there sharing it by tree asks: the leg here holds
                // it as the machine's too.
                holding = [.. holds.HeldOn("a", elsewhere, new SetUpSharing(repository, MachineWide: false)).Select(hold => hold.Leg)];
                return new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed };
            },
            setUps: setUps);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Empty(SetUpScripts.Logged(worktree));
        Assert.Equal([("a", "joined", "left-for-other-legs")], SetUpFacts(outcome));
        Assert.Equal<string>(["other", "native"], holding!);
    }

    /// <summary>
    /// A run asked to set up whose selected legs name no set-up is refused before any host is measured, nothing run: the
    /// option would change nothing while the command line says it did.
    /// </summary>
    [Fact]
    public async Task ARunAskedToSetUp_WhereNothingNamesOne_IsRefusedBeforeAnythingRuns()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        var inspector = SshAndLocal(harness);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a"))),
            inspector,
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload() with { SetUp = true } },
            work: _ => throw new InvalidOperationException("a leg ran for a run refused before anything ran"),
            setUps: SetUpsFor(harness, state).SetUps));

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.StartsWith("--set-up was given, but nothing the selected legs run names a set-up", refused.Message, StringComparison.Ordinal);
        Assert.Empty(inspector.Inspected);
    }

    /// <summary>
    /// A leg another machine runs brings its set-ups up there, never here: the run hands --set-up to the host with the
    /// rest of its command line, and brings nothing up on this machine for that leg.
    /// </summary>
    [Fact]
    public async Task ALegAHostRuns_IsHandedTheOption_AndNothingIsBroughtUpHereForIt()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        HostCommand? sent = null;
        var config = TwoLegs(harness);

        config.SetUp["a"] = SetUpScripts.SetUp("a");
        SetUpScripts.Write(temp.Path);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            sent = command;
            ScriptedHostCommands.Answer(command, """{"legs": [{"leg": "arm", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["arm"], Json: true) { Workload = SetUpWorkload("a") with { SetUp = true } },
            hosts: hosts,
            setUps: SetUpsFor(harness, state).SetUps);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);

        var arguments = JsonSerializer.Deserialize<HostAgentRequest>(sent!.StandardInput.Read(), HostAgentProtocol.JsonOptions)!.Arguments.Select(argument => argument.Text);

        Assert.Contains(LegSetUps.Option, arguments);
        Assert.Empty(SetUpScripts.Logged(temp.Path));
    }

    /// <summary>
    /// A leg brings a set-up up with the values what names it gives its inputs, over their defaults, and brings it down
    /// with the same; its line and document say them.
    /// </summary>
    [Fact]
    public async Task ASetUpNamedWithValues_IsBroughtUpAndDownWithThem()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        var setUp = new SetUpConfig
        {
            Up = SetUpScripts.Line("up-{profile}-{port} 0"),
            Down = SetUpScripts.Line("down-{profile}-{port} 0"),
            Inputs = { ["profile"] = new SetUpInput { Default = "dev" }, ["port"] = new SetUpInput { Default = "1" } },
        };

        SetUpScripts.Write(temp.Path);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", setUp)),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload(new SetUpUse { Name = "A", With = { ["PROFILE"] = "ci" } }) with { SetUp = true } },
            setUps: SetUpsFor(harness, state).SetUps);

        Assert.True(HarnessExit.Success == outcome.ExitCode, $"the run ended {outcome.ExitCode}: {outcome.Message}");
        Assert.Equal<string>(["up-ci-1", "down-ci-1"], SetUpScripts.Logged(temp.Path));
        Assert.Contains("test: native: bringing set-up 'a' (port=1, profile=ci) up", harness.StandardOutput.ToString() + harness.StandardError.ToString(), StringComparison.Ordinal);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var fact = Assert.Single(Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("setUps").EnumerateArray());

        Assert.Equal("ci", fact.GetProperty("inputs").GetProperty("profile").GetString());
    }

    /// <summary>
    /// A run interrupted while a set-up comes up brings it down all the same - what its line started part way may be
    /// running - and the leg's work never starts.
    /// </summary>
    [Fact]
    public async Task AnInterruptedRun_WhileASetUpComesUp_StillBringsItDown()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        using var stop = new CancellationTokenSource();
        var harness = new HarnessFactory();

        SetUpScripts.Write(temp.Path);

        // Interrupted once the up line has started - its log begun - whatever the machine's pace: a timer from the test's
        // start, run late on a busy machine, once landed after the up line had finished. Watched from a thread of its
        // own, as an interrupt comes, so no busy pool holds it back.
        var run = RunId.New();
        var upLog = new LegRecords(new HarnessLayout(temp.Path, temp.Path).RunDirectory(run.Value), "native").PhaseLog(PhaseLogs.NameFor("set-up a up"));
        var watching = new Thread(() =>
        {
            var since = Stopwatch.GetTimestamp();

            while (!File.Exists(upLog) && Stopwatch.GetElapsedTime(since) < TimeSpan.FromMinutes(1))
            {
                Thread.Sleep(10);
            }

            stop.Cancel();
        }) { IsBackground = true };

        watching.Start();

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a", upSeconds: 4))),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload("a") with { SetUp = true } },
            work: _ => throw new InvalidOperationException("the leg's work started while its set-up came up"),
            setUps: SetUpsFor(harness, state).SetUps,
            cancellationToken: stop.Token,
            runId: run);

        Assert.True(HarnessExit.Cancelled == outcome.ExitCode, $"{outcome.ExitCode}: {outcome.Message} | {harness.StandardOutput}{harness.StandardError}");
        Assert.Equal<string>(["down-a"], SetUpScripts.Logged(temp.Path));
        Assert.DoesNotContain("did not bring set-up", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The time a set-up has to come down after an interruption counts from the interruption, as the command line's wait
    /// does, not from when the leg's work let go: one whose turn comes after that time is said as not brought down.
    /// </summary>
    [Fact]
    public async Task TheTimeASetUpHasToComeDown_CountsFromTheInterruption()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        using var stop = new CancellationTokenSource();
        var harness = new HarnessFactory();
        var (setUps, holds) = SetUpsFor(harness, state, grace: TimeSpan.FromMilliseconds(400));

        SetUpScripts.Write(temp.Path);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a"))),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload("a") with { SetUp = true } },
            workAsync: async (leg, _) =>
            {
                await stop.CancelAsync();
                await Task.Delay(TimeSpan.FromSeconds(1.5), CancellationToken.None);
                return new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed };
            },
            setUps: setUps,
            cancellationToken: stop.Token);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Contains("native did not bring set-up 'a' down", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(holds.HeldOn("a", temp.Path));
    }

    /// <summary>
    /// Every set-up a leg names is held before any comes up, in the order of their names, and they come up in the order
    /// named: a leg waiting for one another leg has up with other values holds none named after it, so two legs naming
    /// two set-ups in opposite orders never each hold one and wait for the other.
    /// </summary>
    [Fact]
    public async Task ALegHoldsItsSetUpsInTheOrderOfTheirNames_BeforeAnyComesUp()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        var (setUps, holds) = SetUpsFor(harness, state);
        var token = TestContext.Current.CancellationToken;

        SetUpScripts.Write(temp.Path);
        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("a", new Dictionary<string, string> { ["v"] = "other" }, temp.Path, "another-run", "other", _ => { }, token));
        holds.Stood("a", temp.Path, "another-run", "other");

        var running = OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a")), ("b", SetUpScripts.SetUp("b"))),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload("b", "a") with { SetUp = true } },
            setUps: setUps);

        var waited = Stopwatch.StartNew();

        while (!(harness.StandardOutput.ToString() + harness.StandardError.ToString()).Contains("waiting for set-up 'a'", StringComparison.Ordinal))
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30) && !running.IsCompleted, "the leg never waited for set-up 'a'");
            await Task.Delay(50, token);
        }

        Assert.Empty(SetUpScripts.Logged(temp.Path));
        Assert.Empty(holds.HeldOn("b", temp.Path));

        Assert.True(holds.Leave("a", temp.Path, "another-run", "other"));
        holds.Gone("a", temp.Path, "another-run", "other");

        var outcome = await running;

        Assert.True(HarnessExit.Success == outcome.ExitCode, $"the run ended {outcome.ExitCode}: {outcome.Message}");
        Assert.Equal<string>(["up-b", "up-a", "down-a", "down-b"], SetUpScripts.Logged(temp.Path));
    }

    /// <summary>
    /// A set-up left running by a leg whose work a refusal of the whole run ended, with no line of its own, is said with
    /// the refusal: in the document, and beneath the line the refusal ends on.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASetUpLeftRunning_ByALegARefusalEnded_IsSaidWithTheRefusal(bool json)
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();

        SetUpScripts.Write(temp.Path);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a", downExit: 1))),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: json) { Workload = SetUpWorkload("a") with { SetUp = true } },
            work: _ => throw new HarnessException(HarnessExit.Refused, "an action file this run cannot read. Nothing has run."),
            setUps: SetUpsFor(harness, state).SetUps);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);

        if (json)
        {
            using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
            var left = Assert.Single(document.RootElement.GetProperty("setUpsLeftRunning").EnumerateArray());

            Assert.Equal(("native", "a"), (left.GetProperty("leg").GetString(), left.GetProperty("setUp").GetString()));
        }
        else
        {
            Assert.Contains(
                "native did not bring set-up 'a' down; 'dssharness down a', run in the tree the leg worked in, brings it down",
                outcome.Details ?? []);
        }
    }

    /// <summary>
    /// A set-up whose down line outlasts the time an interrupted run gives it is said as stopped before it came down, with
    /// the command that brings it down, and the leg still lets go of every set-up it held: none of its holds is left to
    /// keep another leg from bringing one up, or a person from bringing one down.
    /// </summary>
    [Fact]
    public async Task ASetUpStoppedComingDown_AfterAnInterruption_IsSaidSo_AndEveryHoldIsLetGo()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        using var stop = new CancellationTokenSource();
        var harness = new HarnessFactory();
        var (setUps, holds) = SetUpsFor(harness, state, grace: TimeSpan.FromMilliseconds(500));

        SetUpScripts.Write(temp.Path);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a")), ("b", SetUpScripts.SetUp("b", downSeconds: 30))),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload("a", "b") with { SetUp = true } },
            work: leg =>
            {
                stop.Cancel();
                return new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed };
            },
            setUps: setUps,
            cancellationToken: stop.Token);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Contains(
            "native did not bring set-up 'b' down; 'dssharness down b', run in the tree the leg worked in, brings it down",
            outcome.Message,
            StringComparison.Ordinal);
        Assert.Empty(holds.HeldOn("a", temp.Path));
        Assert.Empty(holds.HeldOn("b", temp.Path));
    }

    /// <summary>
    /// A set-up whose up line could not start brought nothing up, so nothing is brought down: the leg fails saying why,
    /// and the set-ups before it are brought down.
    /// </summary>
    [Fact]
    public async Task ASetUpWhoseUpLineCouldNotStart_IsNotBroughtDown()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        var missing = new SetUpConfig { Up = "scripts/missing-up", Down = SetUpScripts.Line("down-b 0") };

        SetUpScripts.Write(temp.Path);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a")), ("b", missing)),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload("a", "b") with { SetUp = true } },
            work: _ => throw new InvalidOperationException("the leg's work started after a set-up did not come up"),
            setUps: SetUpsFor(harness, state).SetUps);

        Assert.Equal(HarnessExit.CommandFailed, outcome.ExitCode);
        Assert.StartsWith("set-up 'b' did not come up: 'scripts/missing-up' could not start: ", Verdicts(outcome)["native"].Detail, StringComparison.Ordinal);
        Assert.Equal<string>(["up-a", "down-a"], SetUpScripts.Logged(temp.Path));
        Assert.Equal([("a", "brought-up", "brought-down"), ("b", "failed", null)], SetUpFacts(outcome));
    }

    /// <summary>
    /// A leg stopped with no line still says what it left running: the run's last line and its document name the
    /// set-up whose down failed, and how to bring it down.
    /// </summary>
    [Fact]
    public async Task AStoppedLeg_ThatLeftASetUpRunning_IsNamedInTheRunsLastLine()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        using var stop = new CancellationTokenSource();
        var harness = new HarnessFactory();

        SetUpScripts.Write(temp.Path);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            SetUpConfig(harness, ("a", SetUpScripts.SetUp("a", downExit: 6))),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native"], Json: true) { Workload = SetUpWorkload("a") with { SetUp = true } },
            workAsync: async (leg, token) =>
            {
                await stop.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
                return new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed };
            },
            setUps: SetUpsFor(harness, state).SetUps,
            cancellationToken: stop.Token);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Contains("native did not bring set-up 'a' down; 'dssharness down a', run in the tree the leg worked in, brings it down", outcome.Message, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var left = Assert.Single(document.RootElement.GetProperty("setUpsLeftRunning").EnumerateArray());

        Assert.Equal(["native", "a"], new[] { "leg", "setUp" }.Select(name => left.GetProperty(name).GetString()));
        Assert.Equal("'dssharness down a', run in the tree the leg worked in, brings it down", left.GetProperty("remedy").GetString());
    }

    /// <summary>
    /// A leg another machine runs is handed --set-up only where its work names a set-up: one whose work names none is
    /// run as it would be without it, while this machine's leg brings its own up.
    /// </summary>
    [Fact]
    public async Task ALegAHostRunsThatNamesNoSetUp_IsNotHandedTheOption()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        HostCommand? sent = null;
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig { Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree } } },
            SetUp = { ["a"] = SetUpScripts.SetUp("a") },
            Projects =
            {
                new ProjectConfig { Name = "set", Type = "cmake", Test = new TestConfig { All = new TestInvocation { Runner = "ctest", SuccessPattern = "passed", SetUp = ["a"] } } },
                new ProjectConfig { Name = "plain", Type = "cmake", Test = new TestConfig { All = new TestInvocation { Runner = "ctest", SuccessPattern = "passed" } } },
            },
            Legs =
            {
                ["native"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug", Project = "set" },
                ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = HostName, Project = "plain" },
            },
        };

        SetUpScripts.Write(temp.Path);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            sent = command;
            ScriptedHostCommands.Answer(command, """{"legs": [{"leg": "arm", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["native", "arm"], Json: true) { Workload = new LegWorkload(Build: false, Test: true, []) { SetUp = true } },
            hosts: hosts,
            setUps: SetUpsFor(harness, state).SetUps);

        Assert.True(HarnessExit.Success == outcome.ExitCode, $"the run ended {outcome.ExitCode}: {outcome.Message}");

        var arguments = JsonSerializer.Deserialize<HostAgentRequest>(sent!.StandardInput.Read(), HostAgentProtocol.JsonOptions)!.Arguments.Select(argument => argument.Text);

        Assert.DoesNotContain(LegSetUps.Option, arguments);
        Assert.Equal<string>(["up-a", "down-a"], SetUpScripts.Logged(temp.Path));
    }

    /// <summary>
    /// A set-up a host's leg left running there, where the host's run ended with no line for the leg, is said by this run
    /// as the host's said it: it runs on after both runs.
    /// </summary>
    [Fact]
    public async Task ASetUpAHostsLegLeftRunning_WithNoLineThere_IsSaidHere()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig { Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree } } },
            SetUp = { ["a"] = SetUpScripts.SetUp("a") },
            Projects = { new ProjectConfig { Name = "set", Type = "cmake", Test = new TestConfig { All = new TestInvocation { Runner = "ctest", SuccessPattern = "passed", SetUp = ["a"] } } } },
            Legs = { ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = HostName, Project = "set" } },
        };

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            ScriptedHostCommands.Answer(
                command,
                """{"exitCode":13,"summary":"refused","legs":[],"setUpsLeftRunning":[{"leg":"arm","setUp":"a","up":"brought-up","detail":"'down' exited 1"}]}""");

            return HostResults.Finished(command, HarnessExit.Refused);
        });

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, ["arm"], Json: false) { Workload = new LegWorkload(Build: false, Test: true, []) { SetUp = true } },
            hosts: hosts,
            setUps: SetUpsFor(harness, state).SetUps);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Contains("arm did not bring set-up 'a' down; 'dssharness down a', run in the tree the leg worked in, brings it down", outcome.Details ?? []);
    }

    /// <summary>A leg on this machine whose run names the set-ups given, for a run asked to set up.</summary>
    private static HarnessConfig SetUpConfig(HarnessFactory harness, params (string Name, SetUpConfig SetUp)[] setUps)
    {
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs = { ["native"] = HostDoubles.Leg(harness.Platform.PlatformKey, harness.Platform.Processor) },
        };

        foreach (var (name, setUp) in setUps)
        {
            config.SetUp[name] = setUp;
        }

        return config;
    }

    /// <summary>A command that starts nothing of its own and names <paramref name="setUps"/>, as a runner's do.</summary>
    private static LegWorkload SetUpWorkload(params SetUpUse[] setUps) => new(Build: false, Test: false, []) { SetUps = setUps };

    /// <summary>What brings a leg's set-ups up and down, its record of who holds which kept in <paramref name="state"/>.</summary>
    private static (LegSetUps SetUps, SetUpHolds Holds) SetUpsFor(
        HarnessFactory harness,
        TempDirectory state,
        TimeSpan? grace = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null,
        IProcessRunner? phases = null)
    {
        var holds = new SetUpHolds(harness.FileSystem, harness.Output, harness.Identity, harness.Platform, () => state.Combine("setups.json"), wait: wait);
        var service = new SetUpService(
            harness.ContextLoader,
            harness.ProcessRunner,
            harness.Platform,
            new ActionToolPolicy(harness.Platform),
            HostDoubles.Floors(harness.Platform, harness.ProcessRunner),
            holds,
            harness.Output);

        return (
            new LegSetUps(service, holds, new PhaseRunner(phases ?? harness.ProcessRunner, harness.FileSystem, harness.Output))
            {
                InterruptionGrace = grace ?? WorktreeService.DefaultInterruptionGrace,
            },
            holds);
    }

    /// <summary>A process runner that runs each request through <paramref name="inner"/>, noting the request first.</summary>
    private sealed class RequestsSeen(IProcessRunner inner) : IProcessRunner
    {
        private readonly ConcurrentQueue<ProcessRequest> _seen = new();

        /// <summary>Each request run, in the order it was asked.</summary>
        public IReadOnlyList<ProcessRequest> Seen => [.. _seen];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            _seen.Enqueue(request);
            return inner.RunAsync(request, cancellationToken);
        }

        public string? FindExecutable(string command) => inner.FindExecutable(command);
    }

    /// <summary>What the one leg of <paramref name="outcome"/> did with each set-up, as its document says.</summary>
    private static List<(string? Name, string? Up, string? Down)> SetUpFacts(CommandOutcome outcome)
    {
        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

        return leg.TryGetProperty("setUps", out var setUps)
            ? [.. setUps.EnumerateArray().Select(setUp => (setUp.GetProperty("name").GetString(), setUp.GetProperty("up").GetString(), setUp.TryGetProperty("down", out var down) ? down.GetString() : null))]
            : [];
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
        LegAdmission? admission = null,
        ISyncTransportFactory? transports = null,
        IHarnessContextLoader? loader = null,
        Func<LegWork, LegEntry>? work = null)
    {
        var outcome = await OutcomeAsync(
            temp, harness, config, inspector, request, runLock, sync, ran, keepAwake, developerEnvironments: developerEnvironments, admission: admission,
            transports: transports, loader: loader, work: work);

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
    /// A run whose request carries a watch tells it each leg it placed and where - in a copy of its own, or the tree
    /// itself - before any leg reads the tree; that a leg's copy is made, once its sync has ended and before the leg's
    /// work; and that each leg ended, as its line is kept. A leg in the tree itself has no copy to tell of.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWatchedRun_IsToldEachLegPlaced_ItsCopyMadeBeforeItsWork_AndItsEnd(bool localCopy)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        var config = WatchedConfig(harness, localCopy);

        await harness.InitializeHarnessAsync(temp.Path, token, config);
        temp.WriteFile("src/a.c", "int a;\n");
        await harness.CommitAllAsync(temp.Path, "initial", token);

        var transports = LocalOnlyTransports(harness);
        var said = new SaidWatch();

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null) { Workload = LegWorkload.BuildOnly, Watch = said },
            sync: SyncKit.Service(harness, loader: harness.ContextLoader, transports: transports),
            transports: transports,
            loader: harness.ContextLoader,
            work: work =>
            {
                said.Add($"{work.Leg.Name} works");
                return new LegEntry { Leg = work.Leg.Name, Verdict = LegVerdict.Passed };
            });

        Assert.True(HarnessExit.Success == outcome.ExitCode, $"the run ended {outcome.ExitCode}: {outcome.Message}");
        Assert.Equal(
            localCopy
                ? ["placed native in a copy", "copy of native made", "native works", "native ended"]
                : ["placed native in the tree", "native works", "native ended"],
            said.Said);
    }

    /// <summary>
    /// A leg whose copy's sync fails is never told of as having its copy: its watch hears that it was placed in a copy,
    /// and then that it ended, its line kept - so whoever waits for its copy stops waiting at its end.
    /// </summary>
    [Fact]
    public async Task ALegWhoseCopyIsNeverMade_IsToldOfOnlyAsItEnds()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        var config = WatchedConfig(harness, localCopy: true);

        await harness.InitializeHarnessAsync(temp.Path, token, config);
        temp.WriteFile("src/a.c", "int a;\n");
        await harness.CommitAllAsync(temp.Path, "initial", token);

        var transports = LocalOnlyTransports(harness);
        var real = SyncKit.Service(harness, loader: harness.ContextLoader, transports: transports);
        var failing = Substitute.For<ISyncService>();
        failing.ReadSourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => real.ReadSourceAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        failing.SyncAsync(Arg.Any<SyncSource>(), Arg.Any<ISyncTransport>(), Arg.Any<string>(), Arg.Any<SyncOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<SyncResult>>(_ => throw new IOException("the disk is full"));
        var said = new SaidWatch();

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null) { Workload = LegWorkload.BuildOnly, Watch = said },
            sync: failing,
            transports: transports,
            loader: harness.ContextLoader,
            work: work =>
            {
                said.Add($"{work.Leg.Name} works");
                return new LegEntry { Leg = work.Leg.Name, Verdict = LegVerdict.Passed };
            });

        Assert.NotEqual(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(["placed native in a copy", "native ended"], said.Said);
    }

    /// <summary>One leg this machine runs, in a copy of its own where <paramref name="localCopy"/>, under no limit on its paths.</summary>
    private static HarnessConfig WatchedConfig(HarnessFactory harness, bool localCopy) => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Projects = { new ProjectConfig { Name = "app", Type = "dotnet", Path = "." } },
        Toolchains = { ["gcc"] = new ToolchainConfig { Platforms = [harness.Platform.PlatformKey] } },
        Legs = { ["native"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug", Project = "app", Toolchain = "gcc", LocalCopy = localCopy } },
        Worktrees = new WorktreeSettings { PathLimit = 1000 },
    };

    /// <summary>What a run told its watch, and what its legs did beside it, in order.</summary>
    private sealed class SaidWatch : ILegRunWatch
    {
        private readonly List<string> _said = [];

        /// <summary>Everything said, in order.</summary>
        public IReadOnlyList<string> Said
        {
            get
            {
                lock (_said)
                {
                    return [.. _said];
                }
            }
        }

        public void Placed(IReadOnlyList<LegPlace> legs)
        {
            foreach (var leg in legs)
            {
                Add($"placed {leg.Leg} in {(leg.InACopy ? "a copy" : "the tree")}");
            }
        }

        public void CopyMade(string leg) => Add($"copy of {leg} made");

        public void Ended(string leg) => Add($"{leg} ended");

        public void Add(string said)
        {
            lock (_said)
            {
                _said.Add(said);
            }
        }
    }

    /// <summary>
    /// A leg with localCopy that this machine runs works in a copy of its tree kept in the tree's own .legs, named by its
    /// variant's key: the run reads the tree once, syncs the copy from that reading, and the leg works there - by the
    /// configuration the reading carried, its records in the run's own directory - holding the copy, never the tree. Its
    /// lines say where it works and which reading its copy holds, and the ledger's tree says the same.
    /// </summary>
    [Fact]
    public async Task ALegWithLocalCopy_WorksInACopyOfItsTreeKeptInTheTreesOwnLegs()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        var run = RunId.New();
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects = { new ProjectConfig { Name = "app", Type = "dotnet", Path = "." } },
            Toolchains = { ["gcc"] = new ToolchainConfig { Platforms = [harness.Platform.PlatformKey] } },
            Legs = { ["native"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug", Project = "app", Toolchain = "gcc", LocalCopy = true } },
            Worktrees = new WorktreeSettings { PathLimit = 1000 },
        };

        await harness.InitializeHarnessAsync(temp.Path, token, config);
        temp.WriteFile("src/a.c", "int a;\n");
        await harness.CommitAllAsync(temp.Path, "initial", token);

        var transports = LocalOnlyTransports(harness);
        string? workedIn = null;
        string? ranBy = null;
        BuildRequest? build = null;

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly },
            sync: SyncKit.Service(harness, loader: harness.ContextLoader, transports: transports),
            transports: transports,
            loader: harness.ContextLoader,
            runId: run,
            work: work =>
            {
                workedIn = work.Context.Layout.RepositoryRoot;
                ranBy = work.RunDirectory;
                build = work.Leg.BuildRequestFor(work.Context.Config, work.RunDirectory);
                return new LegEntry { Leg = work.Leg.Name, Verdict = LegVerdict.Passed };
            });

        Assert.True(HarnessExit.Success == outcome.ExitCode, $"the run ended {outcome.ExitCode}: {outcome.Message}");

        var variant = VariantKey.For(config, config.Legs["native"], harness.Platform.PlatformKey);

        var copy = LegCopies.PathOf(temp.Path, variant);

        Assert.True(Directory.Exists(copy), $"no copy was made at '{copy}'");
        Assert.True(File.Exists(Path.Combine(copy, "src", "a.c")), "the copy holds none of the tree");
        Assert.Equal(copy, workedIn);
        Assert.Equal(copy, build!.TreeRoot);
        Assert.Equal(temp.Path, build.CopyOf);
        Assert.StartsWith(Path.Combine(temp.Path, ".harness-config", "runs") + Path.DirectorySeparatorChar, ranBy, StringComparison.Ordinal);

        var lines = (harness.StandardOutput.ToString() + harness.StandardError.ToString()).Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        Assert.Contains($"test: native: starting on local, in its copy '{copy}'", lines);
        Assert.Contains($"test: native: sync of '{copy}' on local", lines);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());
        var tree = leg.GetProperty("tree");

        Assert.Equal(copy, tree.GetProperty("path").GetString());
        Assert.Equal(temp.Path, tree.GetProperty("copyOf").GetString());
        Assert.Equal(run.Value, tree.GetProperty("readBy").GetString());

        // A repository of its own, as every copy a sync makes is: git answers there for the copy, never for the tree.
        Assert.Equal("true", (await harness.RunGitAsync(copy, ["rev-parse", "--is-inside-work-tree"], token)).StandardOutput.Trim());
    }

    /// <summary>
    /// A fold holds the tree it writes, whole - and nothing of a leg that works in a copy of that tree: the two no longer
    /// refuse each other, where the same leg in place is refused for the fold's whole length.
    /// </summary>
    [Fact]
    public async Task AFoldHoldingTheTree_RefusesALegInPlace_AndNotALegInACopyOfIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects = { new ProjectConfig { Name = "app", Type = "dotnet", Path = "." } },
            Toolchains = { ["gcc"] = new ToolchainConfig { Platforms = [harness.Platform.PlatformKey] } },
            Legs = { ["native"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug", Project = "app", Toolchain = "gcc", LocalCopy = true } },
            Worktrees = new WorktreeSettings { PathLimit = 1000 },
        };

        await harness.InitializeHarnessAsync(temp.Path, token, config);
        temp.WriteFile("src/a.c", "int a;\n");
        await harness.CommitAllAsync(temp.Path, "initial", token);

        var layout = new HarnessLayout(temp.Path, temp.Path);

        await using (await new RunLock(harness.FileSystem, harness.Output, harness.Identity).AcquireAsync(
            layout,
            new LockRequest { Host = HostId.Local.ToString(), Tree = temp.Path, Scope = LockScope.TreeExclusive, RunId = RunId.New(), Command = "fold-agent" },
            token))
        {
            var transports = LocalOnlyTransports(harness);
            var verdicts = await RunAsync(
                temp,
                harness,
                config,
                SshAndLocal(harness),
                new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly },
                sync: SyncKit.Service(harness, loader: harness.ContextLoader, transports: transports),
                transports: transports,
                loader: harness.ContextLoader);

            Assert.Equal("passed", verdicts["native"].Verdict);
        }

        var inPlaceConfig = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs = { ["native"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug" } },
        };

        var inPlace = await RunAsync(
            temp,
            harness,
            inPlaceConfig,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly, LocalTrees = LocalTrees.InPlace },
            loader: LocalOnlyLoader(inPlaceConfig, temp.Path));

        // The fold is over, so the leg in place runs in its tree - as it would have been refused while it lasted.
        Assert.Equal("passed", inPlace["native"].Verdict);
    }

    /// <summary>
    /// A run whose leg is to work in a copy, in a tree whose .legs git does not ignore, is refused before anything is
    /// made: naming init, exit 13, with no copy and no reading - as a sweep is for the workers it would make there - and
    /// no claim on a directory of records, so the next run, once the refused one has ended, finds no run abandoned.
    /// </summary>
    [Fact]
    public async Task ARunWhoseLegWouldWorkInACopy_WhereGitDoesNotIgnoreTheLegs_IsRefusedNamingInit()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        var run = RunId.New();

        // The refused run as a process of this machine that has ended by the time the next run starts, as a command's has.
        var ended = Substitute.For<IProcessIdentity>();

        ended.CurrentMachine.Returns(Environment.MachineName);
        ended.CurrentId.Returns(int.MaxValue - 1);
        ended.Current.Returns("a-process-that-has-gone");
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects = { new ProjectConfig { Name = "app", Type = "dotnet", Path = "." } },
            Toolchains = { ["gcc"] = new ToolchainConfig { Platforms = [harness.Platform.PlatformKey] } },
            Legs = { ["native"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug", Project = "app", Toolchain = "gcc", LocalCopy = true } },
            Worktrees = new WorktreeSettings { PathLimit = 1000 },
        };

        await harness.InitializeHarnessAsync(temp.Path, token, config);
        await harness.CommitAllAsync(temp.Path, "initial", token);

        // The tree's ignore rules, as a branch that predates them holds it: nothing ignores .legs.
        var ignore = Path.Combine(temp.Path, ".gitignore");
        File.WriteAllLines(ignore, File.ReadAllLines(ignore).Where(rule => !rule.Contains(HarnessLayout.LegsDirectoryName, StringComparison.Ordinal)));

        var transports = LocalOnlyTransports(harness);
        var read = false;

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly },
            sync: ReadBy(SyncKit.Service(harness, loader: harness.ContextLoader, transports: transports), () => read = true),
            transports: transports,
            logs: new LogOwnership(harness.FileSystem, harness.Output, ended),
            runId: run,
            loader: harness.ContextLoader));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains($"git does not ignore '{HarnessLayout.LegsDirectoryName}/", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"Run '{ToolPackage.Command} init'", refusal.Message, StringComparison.Ordinal);
        Assert.False(read, "the tree was read for a copy that was never made");

        var variant = VariantKey.For(config, config.Legs["native"], harness.Platform.PlatformKey);

        Assert.False(Directory.Exists(LegCopies.PathOf(temp.Path, variant)));

        // No claim and no directory of records: --json names none, and none is there.
        var records = new HarnessLayout(temp.Path, temp.Path).RunDirectory(run.Value);

        Assert.False(File.Exists(LogOwnership.OwnerFile(records)), "the refused run left its claim on its directory of records");
        Assert.False(Directory.Exists(records), "the refused run left a directory of records it never named");

        // The next run, its leg in place, says no earlier run was abandoned: the refused one was not.
        var before = harness.StandardError.ToString().Length;

        var next = await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly, LocalTrees = LocalTrees.InPlace },
            loader: harness.ContextLoader);

        Assert.Equal(HarnessExit.Success, next.ExitCode);
        Assert.DoesNotContain("WARN", harness.StandardError.ToString()[before..], StringComparison.Ordinal);
    }

    /// <summary>
    /// A directory this tool did not make where a leg's copy is kept refuses the run, exit 13, in the words a host's
    /// copy is refused - with no takeover offered, for none is: move it away, or run the leg in its tree - and the
    /// directory is left as it was.
    /// </summary>
    [Fact]
    public async Task ADirectoryThisToolDidNotMake_AtTheCopysPath_RefusesTheRunAndIsLeftAsItWas()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects = { new ProjectConfig { Name = "app", Type = "dotnet", Path = "." } },
            Toolchains = { ["gcc"] = new ToolchainConfig { Platforms = [harness.Platform.PlatformKey] } },
            Legs = { ["native"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug", Project = "app", Toolchain = "gcc", LocalCopy = true } },
            Worktrees = new WorktreeSettings { PathLimit = 1000 },
        };

        await harness.InitializeHarnessAsync(temp.Path, token, config);
        await harness.CommitAllAsync(temp.Path, "initial", token);

        var variant = VariantKey.For(config, config.Legs["native"], harness.Platform.PlatformKey);
        var somebodys = LegCopies.PathOf(temp.Path, variant);

        Directory.CreateDirectory(Path.Combine(somebodys, "mine"));
        File.WriteAllText(Path.Combine(somebodys, "mine", "a.c"), "int a;\n");

        var transports = LocalOnlyTransports(harness);

        var outcome = await OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly },
            sync: SyncKit.Service(harness, loader: harness.ContextLoader, transports: transports),
            transports: transports,
            loader: harness.ContextLoader);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Contains($"'{somebodys}' on local exists and the harness did not create it", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("move it away, or run the leg with --in-place. Nothing was changed.", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--adopt", outcome.Message, StringComparison.Ordinal);

        Assert.Equal("int a;\n", await File.ReadAllTextAsync(Path.Combine(somebodys, "mine", "a.c"), token));
        Assert.Equal(["mine"], Directory.GetFileSystemEntries(somebodys).Select(Path.GetFileName));
    }

    /// <summary>
    /// A leg this machine ran names its own directory of records and the logs in it - the line said as its verdict is and
    /// the run's closing line the same sentence - and its JSON holds them as <c>records</c> and <c>logs</c>, with no
    /// <c>hostRunId</c>, which a leg here has not.
    /// </summary>
    [Fact]
    public async Task ALegThisMachineRan_NamesItsOwnRecords_InItsVerdictLineItsClosingLinesAndItsJson()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLeg(harness);
        var kept = new List<string>();

        async Task<CommandOutcome> RunOnceAsync(bool json)
            => await OutcomeAsync(
                temp,
                harness,
                config,
                SshAndLocal(harness),
                new LegRunRequest(temp.Path, null, Json: json) { Workload = LegWorkload.BuildOnly },
                loader: LocalOnlyLoader(config, temp.Path),
                work: work =>
                {
                    var records = new LegRecords(work.RunDirectory, work.Leg.Name);

                    Directory.CreateDirectory(records.Directory);
                    File.WriteAllText(records.PhaseLog("configure"), "cmake\n");
                    File.WriteAllText(records.PhaseLog("build"), "cc\n");
                    File.WriteAllText(records.PhaseLog("test"), "tests\n");
                    kept.Add(records.Directory);

                    return new LegEntry { Leg = work.Leg.Name, Verdict = LegVerdict.Passed };
                });

        var asJson = await RunOnceAsync(json: true);

        Assert.Equal(HarnessExit.Success, asJson.ExitCode);

        var leg = Assert.Single(JsonDocument.Parse(Assert.Single(asJson.Data)).RootElement.GetProperty("legs").EnumerateArray());

        Assert.Equal(kept[0], leg.GetProperty("records").GetString());
        Assert.Equal(
            [("build", "build.log"), ("configure", "configure.log"), ("test", "test.log")],
            leg.GetProperty("logs").EnumerateArray().Select(log => (log.GetProperty("phase").GetString(), Path.GetFileName(log.GetProperty("file").GetString()))));
        Assert.False(leg.TryGetProperty("hostRunId", out _));

        var asText = await RunOnceAsync(json: false);

        Assert.Equal(HarnessExit.Success, asText.ExitCode);

        // The closing block's line, and the one said as the verdict is - the same sentence, prefixed as a leg's line.
        Assert.Equal(
            [$"logs of native: {kept[1]} (build.log, configure.log, test.log)"],
            (asText.Details ?? []).Where(line => line.StartsWith("logs of ", StringComparison.Ordinal)).ToList(),
            StringComparer.Ordinal);

        var lines = (harness.StandardOutput.ToString() + harness.StandardError.ToString()).Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        Assert.Equal(
            [$"test: native: logs of native: {kept[0]} (build.log, configure.log, test.log)", $"test: native: logs of native: {kept[1]} (build.log, configure.log, test.log)"],
            lines.Where(line => line.Contains("logs of native", StringComparison.Ordinal)).ToList(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// A run whose legs a lock another run holds turned away ends 13, and says beside its code that the lock is why: the
    /// one refusal a caller that can wait waits out rather than puts right - in its ledger and as text alike.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARunWhoseLegsAHeldLockTurnedAway_SaysTheLockIsWhy(bool json)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLeg(harness);
        var layout = new HarnessLayout(temp.Path, temp.Path);

        await using (await new RunLock(harness.FileSystem, harness.Output, harness.Identity).AcquireAsync(
            layout,
            new LockRequest { Host = HostId.Local.ToString(), Tree = temp.Path, Scope = LockScope.TreeExclusive, RunId = RunId.New(), Command = "fold-agent" },
            TestContext.Current.CancellationToken))
        {
            var outcome = await OutcomeAsync(
                temp,
                harness,
                config,
                SshAndLocal(harness),
                new LegRunRequest(temp.Path, null, Json: json) { Workload = LegWorkload.BuildOnly },
                loader: LocalOnlyLoader(config, temp.Path));

            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.Equal(OutcomeCause.LockHeld, outcome.Cause);
        }
    }

    /// <summary>
    /// A run that ends 13 for anything but a lock its legs met, or ends otherwise with a leg a lock turned away among
    /// worse, says nothing of a lock: only where a held lock is what its code says is there a run to wait for.
    /// </summary>
    [Theory]
    [InlineData(LegVerdict.Passed, HarnessExit.Success)]
    [InlineData(LegVerdict.Failed, HarnessExit.CommandFailed)]
    [InlineData(LegVerdict.Poisoned, HarnessExit.InternalError)]
    [InlineData(LegVerdict.RefusedLocked, HarnessExit.Refused)]
    public async Task OnlyACodeAHeldLockDecides_SaysTheLockIsWhy(LegVerdict verdict, int exitCode)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        var outcome = await OutcomeAsync(
            temp,
            harness,
            OneLeg(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null) { Workload = LegWorkload.Copy },
            work: leg => new LegEntry { Leg = leg.Leg.Name, Verdict = verdict, Detail = "as the leg's work said" });

        Assert.Equal(exitCode, outcome.ExitCode);
        Assert.Equal(verdict == LegVerdict.RefusedLocked ? OutcomeCause.LockHeld : OutcomeCause.None, outcome.Cause);
    }

    /// <summary>A leg refused before its work began kept no records: no line names any, and its JSON holds none.</summary>
    [Fact]
    public async Task ALegRefusedBeforeItsWork_NamesNoRecords()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLeg(harness);
        var layout = new HarnessLayout(temp.Path, temp.Path);

        await using (await new RunLock(harness.FileSystem, harness.Output, harness.Identity).AcquireAsync(
            layout,
            new LockRequest { Host = HostId.Local.ToString(), Tree = temp.Path, Scope = LockScope.TreeExclusive, RunId = RunId.New(), Command = "fold-agent" },
            TestContext.Current.CancellationToken))
        {
            var outcome = await OutcomeAsync(
                temp,
                harness,
                config,
                SshAndLocal(harness),
                new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly },
                loader: LocalOnlyLoader(config, temp.Path));

            var leg = Assert.Single(JsonDocument.Parse(Assert.Single(outcome.Data)).RootElement.GetProperty("legs").EnumerateArray());

            Assert.Equal("refused-locked", leg.GetProperty("verdict").GetString());
            Assert.False(leg.TryGetProperty("records", out _));
            Assert.False(leg.TryGetProperty("logs", out _));
            Assert.DoesNotContain("logs of native", harness.StandardOutput.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A leg in place that ends inputs-moved names the remedy after its detail - the leg's key, the default, the option -
    /// where the command could have run it on a copy; a leg that worked in a copy does not, nor a command that takes no
    /// such option, nor a leg no command could run on a copy of its own: one another machine sent here, which works in
    /// the copy that machine made whatever its key says, and one typed in a copy a sync made.
    /// </summary>
    [Theory]
    [InlineData(false, null, false, true)]
    [InlineData(true, null, false, false)]
    [InlineData(false, "ssh", false, false)]
    [InlineData(false, "wsl", false, false)]
    [InlineData(false, null, true, false)]
    public async Task ALegInPlaceThatEndsInputsMoved_NamesTheRemedy_WhereTheCommandCouldHaveRunItOnACopy(
        bool inPlace,
        string? sentBy,
        bool syncedCopy,
        bool namesIt)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLeg(harness);

        config.Hosts.Ssh[HostName] = new SshHostConfig { RepositoryPath = HostTree };
        config.Hosts.Wsl["Ubuntu"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" };

        HostId? here = sentBy switch
        {
            null => null,
            "ssh" => HostId.Ssh(HostName),
            "wsl" => HostId.Wsl("Ubuntu"),
            _ => throw new ArgumentOutOfRangeException(nameof(sentBy), sentBy, "no host of that kind sends a leg here"),
        };

        var verdicts = await RunAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true, Here: here)
            {
                Workload = LegWorkload.BuildOnly,
                LocalTrees = inPlace ? LocalTrees.InPlace : LocalTrees.AsConfigured,
            },
            loader: HostDoubles.Loader(config, temp.Path, syncedCopy: syncedCopy),
            work: _ => new LegEntry { Leg = "native", Verdict = LegVerdict.InputsMoved, Detail = "1 input changed: src/a.c" });

        Assert.Equal("inputs-moved", verdicts["native"].Verdict);

        if (namesIt)
        {
            Assert.EndsWith(
                $"1 input changed: src/a.c; the leg worked in '{temp.Path}' itself - legs.native.localCopy, defaults.localCopy for every leg, "
                + "or --local-copy for one run, runs it on a copy a later write does not reach",
                verdicts["native"].Detail,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("1 input changed: src/a.c", verdicts["native"].Detail);
        }
    }

    /// <summary>The transports of a run that reaches no host: this machine's own, for its legs' copies.</summary>
    private static ISyncTransportFactory LocalOnlyTransports(HarnessFactory harness)
    {
        var transports = Substitute.For<ISyncTransportFactory>();

        transports.For(Arg.Any<HostReport>()).Returns(call => call.Arg<HostReport>().Host.Kind == HostKind.Local
            ? (ISyncTransport)SyncKit.Transport(harness)
            : throw new InvalidOperationException("this test reaches no host"));

        return transports;
    }

    /// <summary>A loader that answers no copy: a run whose legs work in their trees, whatever configuration it is given.</summary>
    private static IHarnessContextLoader LocalOnlyLoader(HarnessConfig config, string root) => HostDoubles.Loader(config, root);

    /// <summary>A sync service that notes, in <paramref name="read"/>, that a tree was read through it.</summary>
    private static ISyncService ReadBy(ISyncService sync, Action read)
    {
        var wrapped = Substitute.For<ISyncService>();

        wrapped.ReadSourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => { read(); return sync.ReadSourceAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)); });

        wrapped.SyncAsync(Arg.Any<SyncSource>(), Arg.Any<ISyncTransport>(), Arg.Any<string>(), Arg.Any<SyncOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => sync.SyncAsync(call.ArgAt<SyncSource>(0), call.ArgAt<ISyncTransport>(1), call.ArgAt<string>(2), call.ArgAt<SyncOptions>(3), call.ArgAt<CancellationToken>(4)));

        return wrapped;
    }

    /// <summary>
    /// Runs the command over <paramref name="config"/>, a leg on this machine passing unless
    /// <paramref name="work"/> says otherwise, and returns how it ended. The tree is the repository's
    /// root unless <paramref name="tree"/> names a worktree of it, and the run is <paramref name="runId"/> where one is given.
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
        IWslPageCache? pageCache = null,
        IRepositoryTrees? trees = null,
        CancellationToken? cancellationToken = null,
        RunId? runId = null,
        IHarnessContextLoader? loader = null,
        LegSetUps? setUps = null,
        IProcessRunner? versionProbes = null,
        IHarnessOutput? output = null,
        RunStops? stops = null)
    {
        var loading = loader ?? HostDoubles.Loader(config, tree ?? temp.Path, temp.Path);
        var run = runId ?? RunId.New();
        var claims = logs ?? new LogOwnership(harness.FileSystem, harness.Output, harness.Identity);

        var service = new LegRunService(
            loading,
            new LegsService(loading, inspector, new KnownTrees(), harness.Platform, harness.Output),
            new LegExecutor(harness.Platform, harness.Output),
            runLock ?? new RunLock(harness.FileSystem, harness.Output, harness.Identity),
            claims,
            sync ?? SyncKit.ServiceDouble(),

            // Each host's copy as this run's sync leaves it, unless the test says what it holds: a sync double writes nothing.
            transports ?? SyncKit.CopiesHolding(new CopyReading(run.Value, null, 0)),
            new RemoteLegRunner(hosts ?? new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command)), harness.Output),

            // Never this machine's own record of its heavy legs: a test's slots are its own.
            admission ?? AdmissionKit.Admission(harness, AdmissionRecord(temp), new ScriptedGauge(10), new ManualClock()),
            pageCache ?? new ScriptedPageCache(),
            trees ?? new KnownTrees(),
            new KeepAwake(keepAwake ?? new HeldProcesses(), harness.Output),
            developerEnvironments ?? NoDeveloperEnvironment(harness),
            HostDoubles.Floors(harness.Platform, versionProbes ?? harness.ProcessRunner, harness.FilePermissions),
            harness.GitClient,
            harness.PathBudget,
            fileSystem ?? harness.FileSystem,
            harness.Platform,
            output ?? harness.Output,
            stops ?? new RunStops(harness.FileSystem, harness.Identity, harness.Output, claims),
            origin,
            setUps);

        return await service.RunAsync(
            "test",
            run,
            request,
            (leg, token) =>
            {
                ran?.Invoke(leg.Leg);
                return workAsync?.Invoke(leg, token)
                    ?? Task.FromResult(work?.Invoke(leg) ?? new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed });
            },
            cancellationToken ?? TestContext.Current.CancellationToken);
    }

    /// <summary>A front end's sink of events, keeping each as it is given.</summary>
    private sealed class RecordingSink : IRunEvents
    {
        public List<RunEvent> Seen { get; } = [];

        public void Emit(RunEvent @event) => Seen.Add(@event);
    }
}
