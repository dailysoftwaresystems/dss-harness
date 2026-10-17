using System.Globalization;
using System.Text.Json;
using NSubstitute;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runs;
using DssHarness.Core.Sync;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

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

        Assert.Equal(LegsExit.Unavailable, outcome.ExitCode);
        Assert.EndsWith($"'{Path.Combine("~", "gone")}' could not be read", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(temp.Path, leg.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A dry run says what each build directory holds and the room beside it, and removes nothing. What it measures of
    /// the build directory never reaches the tree's slot for workers: a worker's build there is counted as the worker's
    /// alone.
    /// </summary>
    [Fact]
    public async Task ADryRun_SaysWhatTheBuildDirectoryHolds_AndRemovesNothing()
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var directory = BuildDirectory(temp, harness, config);
        var file = Written(Path.Combine(directory, "a.o"));
        File.WriteAllText(file, new string('a', 2048));
        workers.Made(Variant(harness, config), 1, 4096);

        var (outcome, leg) = await CleanAsync(temp, harness, config, dryRun: true);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.StartsWith($"2 KiB in '{directory}'; ", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(leg.GetProperty("space").GetProperty("removed").GetBoolean());
        Assert.Equal(2048, leg.GetProperty("space").GetProperty("buildBytes").GetInt64());
        Assert.Equal(Workers.Holding(4096), leg.GetProperty("space").GetProperty("workerBytes").GetInt64());
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

        // A build in the way is waited out, not put right: the one thing the code cannot say, said beside it.
        Assert.Equal(OutcomeCause.LockHeld, outcome.Cause);
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

        var request = JsonSerializer.Deserialize<HostAgentRequest>(sent!.StandardInput.Read(), HostAgentProtocol.JsonOptions)!;

        Assert.Equal(HostTree, request.Directory);
        Assert.Equal(
            ["clean", "--legs", "arm", "--json", RemoteLegRunner.HereOption, $"ssh {HostName}", .. dryRun ? new[] { CleanService.DryRunOption } : []],
            request.Arguments);
    }

    /// <summary>
    /// A leg its host left alone for a lock held there - a sweep still running on it - is refused-locked, as it is when
    /// this machine holds the lock: the run reports on its legs and is no refusal, though that verdict's code is one.
    /// </summary>
    [Fact]
    public async Task ALegItsHostLeftForALockHeldThere_IsRefusedLocked_AndTheRunIsNoRefusal()
    {
        const string Why = "a sweep still running holds its mutation workers: pid 4242, run 20261007-101500-abcd";

        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var report = LedgerReport.From([new LegEntry { Leg = "arm", Verdict = LegVerdict.RefusedLocked, Detail = Why }], durationWarningFactor: 0);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke(FailureLine.For(CleanService.CommandName, report.Summarize(cancelled: false, unfinished: [])));
            ScriptedHostCommands.Answer(command, report.ToJson(cancelled: false, unfinished: []));

            return HostResults.Finished(command, report.ExitCodeGiven(cancelled: false, unfinished: []));
        });

        var (outcome, leg) = await CleanAsync(temp, harness, OneHostLeg(), OnTheHost(), hosts: hosts, copyThere: true);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));

        Assert.Equal(Verdicts.ExitCodeFor(LegVerdict.RefusedLocked), outcome.ExitCode);
        Assert.Equal("refused-locked", document.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("refused-locked: 1 of 1 leg(s)", document.RootElement.GetProperty("summary").GetString());
        Assert.Equal(("refused-locked", $"ssh {HostName}: {Why}"), (leg.GetProperty("verdict").GetString(), leg.GetProperty("detail").GetString()));
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
        Assert.False(leg.TryGetProperty("space", out _));
        Assert.Empty(hosts.Calls);
    }

    /// <summary>
    /// A host holding no copy of the tree holds none of its mutation workers either - they were kept in the copy - so it
    /// is asked to remove none, in a dry run or not: what an earlier version of this tool left beside where the copy was
    /// is known to nothing here, and is a person's to delete.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALegOnAHostWithNoCopyOfTheTree_AsksTheHostToRemoveNoWorker(bool dryRun)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var transport = Substitute.For<ISyncTransport>();
        var transports = Substitute.For<ISyncTransportFactory>();

        transport.RootExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        transports.For(Arg.Any<HostReport>()).Returns(transport);

        var (outcome, leg) = await CleanAsync(temp, harness, OneHostLeg(), OnTheHost(), dryRun: dryRun, transports: transports);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("passed", leg.GetProperty("verdict").GetString());
        Assert.Equal($"nothing to remove: ssh {HostName} holds no copy of this tree at '{HostTree}'", leg.GetProperty("detail").GetString());
        Assert.False(leg.TryGetProperty("space", out _));
        await transport.DidNotReceiveWithAnyArgs().RemoveWorkersAsync(default!, default, TestContext.Current.CancellationToken);
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
    /// A leg on a host that a sweep of this machine's is sweeping there, in the copy of its own it works in, is
    /// refused-locked here, and the host - whose clean would remove that copy - is asked nothing.
    /// </summary>
    [Fact]
    public async Task ALegOnAHostASweepOfThisMachineHoldsInACopyOfItsOwn_IsRefusedLocked_BeforeTheHostIsAsked()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var hosts = new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command));
        var transports = Substitute.For<ISyncTransportFactory>();
        var copy = LegCopies.PathOf(HostTree, VariantKey.For(OneHostLeg(), OneHostLeg().Legs["arm"], "linux"));

        await using var held = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            LegCopies.Whole(copy, MutationService.CommandName, HostId.Ssh(HostName)),
            TestContext.Current.CancellationToken);

        var (_, leg) = await CleanAsync(temp, harness, OneHostLeg(), OnTheHost(), runLock, hosts, transports: transports);

        Assert.Equal("refused-locked", leg.GetProperty("verdict").GetString());
        Assert.Contains(held.Entry.Holder.RunId, leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(hosts.Calls);
        transports.DidNotReceiveWithAnyArgs().For(default!);
    }

    /// <summary>
    /// --former-workers removes the workers an earlier version of this tool kept beside each tree of the repository on
    /// this machine - the main checkout and every worktree - and nothing else: not the leg's build directory, not a
    /// directory under such a name that no sync made, which is said and left, not one beside another repository's tree.
    /// A dry run says each with its size and removes nothing; once a machine, whichever legs run there.
    /// </summary>
    [Fact]
    public async Task FormerWorkers_AreRemovedFromBesideEachTreeHere_AndNothingElse()
    {
        using var temp = new TempDirectory();
        using var worktree = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var transport = SyncKit.Transport(harness);
        var token = TestContext.Current.CancellationToken;
        var directory = BuildDirectory(temp, harness, config);
        var mine = temp.Path + MutationWorkers.FormerFamily + "357e24cw-1";
        var worktrees = worktree.Path + MutationWorkers.FormerFamily + "357e24cs-2";
        var somebodys = temp.Path + MutationWorkers.FormerFamily + "357e24cw-3";
        var anothers = temp.Path + "-other" + MutationWorkers.FormerFamily + "357e24cw-1";
        var legCopy = new RepositoryTree(Path.Combine(temp.Path, ".legs", "357e24c"), "the main checkout's leg copy") { LegCopy = true };
        var trees = new KnownTrees { Beside = host => host == HostId.Local ? [new RepositoryTree(worktree.Path, "worktree w"), legCopy] : [] };

        File.WriteAllText(Written(Path.Combine(directory, "a.o")), "object");

        try
        {
            foreach (var made in new[] { mine, worktrees, anothers })
            {
                await transport.CreateRootAsync(made, CopyMark.Complete, token);
                File.WriteAllText(Path.Combine(made, "main.c"), new string('m', 100));
            }

            File.WriteAllText(Written(Path.Combine(somebodys, "notes.txt")), "somebody's");

            var (measured, dry) = await CleanAsync(temp, harness, config, dryRun: true, trees: trees, formerWorkers: true);
            var said = dry.GetProperty("detail").GetString()!;

            Assert.Equal(HarnessExit.Success, measured.ExitCode);
            Assert.StartsWith("2 worker(s) an earlier version kept beside its trees, ", said, StringComparison.Ordinal);
            Assert.Contains($"'{mine}'", said, StringComparison.Ordinal);
            Assert.Contains($"'{worktrees}'", said, StringComparison.Ordinal);
            Assert.Contains($"'{somebodys}': nothing there says the harness made it, so it is yours to remove", said, StringComparison.Ordinal);
            Assert.All(new[] { mine, worktrees }, path => Assert.True(Directory.Exists(path), path));

            var (outcome, leg) = await CleanAsync(temp, harness, config, trees: trees, formerWorkers: true);

            Assert.Equal(HarnessExit.Success, outcome.ExitCode);
            Assert.StartsWith("removed 2 worker(s) an earlier version kept beside its trees, ", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.True(leg.GetProperty("space").GetProperty("workerBytes").GetInt64() > 0);
            Assert.All(new[] { mine, worktrees }, path => Assert.False(Directory.Exists(path), path));
            Assert.All(new[] { somebodys, anothers, directory }, path => Assert.True(Directory.Exists(path), path));

            var (_, again) = await CleanAsync(temp, harness, config, trees: trees, formerWorkers: true);

            Assert.Equal($"no worker an earlier version kept beside '{temp.Path}', '{worktree.Path}' is there; '{somebodys}': nothing there says the harness made it, so it is yours to remove", again.GetProperty("detail").GetString());
        }
        finally
        {
            foreach (var path in new[] { mine, worktrees, somebodys, anothers }.Where(Directory.Exists))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    /// <summary>
    /// Where the worktrees cannot be listed, --former-workers still removes the workers beside the trees it knows - the
    /// one the command is typed in, and the main checkout - and the leg fails, saying the rest were not looked for.
    /// </summary>
    [Fact]
    public async Task FormerWorkers_BesideTheTreesKnown_AreRemoved_WhereTheWorktreesCannotBeListed_AndTheLegFailsSayingSo()
    {
        using var main = new TempDirectory();
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var mine = temp.Path + MutationWorkers.FormerFamily + "357e24cw-1";
        var mains = main.Path + MutationWorkers.FormerFamily + "357e24cw-1";

        try
        {
            foreach (var made in new[] { mine, mains })
            {
                await SyncKit.Transport(harness).CreateRootAsync(made, CopyMark.Complete, TestContext.Current.CancellationToken);
            }

            var (outcome, leg) = await CleanAsync(
                temp, harness, config, trees: new KnownTrees { Unlisted = "git worktree list exited 128" }, formerWorkers: true, mainCheckout: main.Path);
            var said = leg.GetProperty("detail").GetString()!;

            Assert.NotEqual(HarnessExit.Success, outcome.ExitCode);
            Assert.Equal("failed", leg.GetProperty("verdict").GetString());
            Assert.StartsWith("removed 2 worker(s) an earlier version kept beside its trees, ", said, StringComparison.Ordinal);
            Assert.EndsWith($": '{mine}', '{mains}'; the worktrees beside the main checkout could not be listed, so none beside them was looked for: git worktree list exited 128", said, StringComparison.Ordinal);
            Assert.False(Directory.Exists(mine));
            Assert.False(Directory.Exists(mains));
        }
        finally
        {
            foreach (var path in new[] { mine, mains }.Where(Directory.Exists))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    /// <summary>
    /// Where the worktrees deleted since cannot be looked for - the worktrees root cannot be listed - --former-workers
    /// still removes the workers beside the trees it knows, and the leg fails, saying why, rather than ending poisoned
    /// with nothing removed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormerWorkers_BesideTheTreesKnown_AreRemoved_WhereTheWorktreesDeletedSinceCannotBeLookedFor(bool denied)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var root = (await HostDoubles.Loader(config, temp.Path).LoadAsync(temp.Path, TestContext.Current.CancellationToken)).Layout.WorktreesDirectoryUnder(config.Worktrees.Root);
        var mine = temp.Path + MutationWorkers.FormerFamily + "357e24cw-1";

        Directory.CreateDirectory(root);

        try
        {
            await SyncKit.Transport(harness).CreateRootAsync(mine, CopyMark.Complete, TestContext.Current.CancellationToken);

            var (outcome, leg) = await CleanAsync(temp, harness, config, fileSystem: new CannotList(harness.FileSystem, root, denied), formerWorkers: true);
            var said = leg.GetProperty("detail").GetString()!;

            Assert.NotEqual(HarnessExit.Success, outcome.ExitCode);
            Assert.Equal("failed", leg.GetProperty("verdict").GetString());
            Assert.StartsWith("removed 1 worker(s) an earlier version kept beside its trees, ", said, StringComparison.Ordinal);
            Assert.EndsWith("; the worktrees deleted since could not be looked for, so none beside them was: the disk would not say", said, StringComparison.Ordinal);
            Assert.False(Directory.Exists(mine));
        }
        finally
        {
            if (Directory.Exists(mine))
            {
                Directory.Delete(mine, recursive: true);
            }
        }
    }

    /// <summary>
    /// The workers beside one tree reached by two spellings - a separator after it, a link along the way to it - are said
    /// once: measured once for each spelling, a dry run counted them, and the room they hold, twice.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormerWorkers_BesideOneTreeSpeltTwoWays_AreSaidOnce(bool throughALink)
    {
        using var temp = new TempDirectory();
        using var place = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var real = Directory.CreateDirectory(Path.Combine(place.Path, "real")).FullName;
        var tree = Path.Combine(real, "wt");
        var worker = tree + MutationWorkers.FormerFamily + "357e24cw-1";
        var again = tree + Path.DirectorySeparatorChar;

        if (throughALink)
        {
            TestLinks.DirectoryLink(Path.Combine(place.Path, "linked"), real);
            again = Path.Combine(place.Path, "linked", "wt");
        }

        await SyncKit.Transport(harness).CreateRootAsync(worker, CopyMark.Complete, TestContext.Current.CancellationToken);

        var trees = new KnownTrees
        {
            Beside = host => host == HostId.Local ? [new RepositoryTree(tree, "worktree w"), new RepositoryTree(again, "worktree w, spelt again")] : [],
        };
        var (_, dry) = await CleanAsync(temp, harness, config, dryRun: true, trees: trees, formerWorkers: true);

        Assert.StartsWith("1 worker(s) an earlier version kept beside its trees, ", dry.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// --former-workers stopped part way says which went and that it was stopped, the leg stopped with it - also where a
    /// tree after the one it was stopped in holds none to stop in - and leaves the rest for the next clean.
    /// </summary>
    [Fact]
    public async Task FormerWorkers_StoppedPartWay_SayWhichWent_AndTheLegIsStopped()
    {
        using var temp = new TempDirectory();
        using var worktree = new TempDirectory();
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var first = temp.Path + MutationWorkers.FormerFamily + "357e24cw-1";
        var second = temp.Path + MutationWorkers.FormerFamily + "357e24cw-2";

        try
        {
            foreach (var made in new[] { first, second })
            {
                await SyncKit.Transport(harness).CreateRootAsync(made, CopyMark.Complete, TestContext.Current.CancellationToken);
            }

            var (_, leg) = await CleanAsync(
                temp,
                harness,
                config,
                fileSystem: new InterruptsOnceRemoved(harness.FileSystem, first, interruption),
                trees: new KnownTrees { Beside = host => host == HostId.Local ? [new RepositoryTree(worktree.Path, "worktree w")] : [] },
                formerWorkers: true,
                cancellationToken: interruption.Token);
            var said = leg.GetProperty("detail").GetString()!;

            Assert.Equal("stopped", leg.GetProperty("verdict").GetString());
            Assert.StartsWith($"removed 1 worker(s) an earlier version kept beside its trees, ", said, StringComparison.Ordinal);
            Assert.EndsWith(LocalSyncTransport.StoppedPartWay, said, StringComparison.Ordinal);
            Assert.False(Directory.Exists(first));
            Assert.True(Directory.Exists(second));
        }
        finally
        {
            foreach (var path in new[] { first, second }.Where(Directory.Exists))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    /// <summary>
    /// --former-workers on a leg a host runs is asked of the host from its home, beside its copies of the repository kept
    /// together, whatever copy of it the host holds - none, even, every one removed since - and --dry-run with it: no
    /// command is run in a tree there, and what went is said as here.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormerWorkers_OfALegOnAHost_AreAskedOfTheHost_BesideItsCopies_WhateverCopyItHolds(bool dryRun)
    {
        using var temp = new TempDirectory();
        var transport = Substitute.For<ISyncTransport>();
        var transports = Substitute.For<ISyncTransportFactory>();
        var hosts = new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command));
        var worker = HostTree + HostCopies.WorktreeSuffix + "gone" + MutationWorkers.FormerFamily + "357e24cw-1";

        transport.RemoveFormerWorkersBesideCopiesAsync(HostTree, dryRun, Arg.Any<CancellationToken>())
            .Returns(new WorkersRemoval([new WorkerRemoved(worker, 2048)], []));
        transports.For(Arg.Any<HostReport>()).Returns(transport);

        var (outcome, leg) = await CleanAsync(temp, new HarnessFactory(), OneHostLeg(), OnTheHost(), hosts: hosts, transports: transports, dryRun: dryRun, formerWorkers: true);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(
            $"{(dryRun ? string.Empty : "removed ")}1 worker(s) an earlier version kept beside its trees, 2 KiB: '{worker}'",
            leg.GetProperty("detail").GetString());
        Assert.Empty(hosts.Calls);
        _ = transport.DidNotReceiveWithAnyArgs().RootExistsAsync(default!, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// --former-workers on a host keeping no worker of an earlier version says where it looked; one whose copies cannot
    /// be looked in fails the leg, saying why; and one that cannot be reached leaves it unavailable, as any leg there.
    /// </summary>
    [Fact]
    public async Task FormerWorkers_OfALegOnAHost_SayWhereTheyWereLookedFor_AndWhyNot()
    {
        using var temp = new TempDirectory();
        var answers = new Queue<Func<WorkersRemoval>>(
        [
            () => WorkersRemoval.None,
            () => throw new HarnessException(HarnessExit.CommandFailed, "'/home/pi' could not be looked in: Permission denied"),
            () => throw new HarnessException(HarnessExit.HostUnavailable, "ssh pi: the host did not answer"),
        ]);
        var transport = Substitute.For<ISyncTransport>();
        var transports = Substitute.For<ISyncTransportFactory>();

        transport.RemoveFormerWorkersBesideCopiesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => answers.Dequeue()());
        transports.For(Arg.Any<HostReport>()).Returns(transport);

        async Task<(string? Verdict, string? Detail)> CleanedAsync()
        {
            var (_, leg) = await CleanAsync(temp, new HarnessFactory(), OneHostLeg(), OnTheHost(), transports: transports, formerWorkers: true);
            return (leg.GetProperty("verdict").GetString(), leg.GetProperty("detail").GetString());
        }

        Assert.Equal(("passed", $"no worker an earlier version kept beside '{HostTree}' or a copy beside it is there"), await CleanedAsync());
        Assert.Equal(("failed", "'/home/pi' could not be looked in: Permission denied"), await CleanedAsync());
        Assert.Equal("skipped-unavailable", (await CleanedAsync()).Verdict);
    }

    /// <summary>
    /// --former-workers removes the workers an earlier version kept beside a worktree deleted since, wherever in the
    /// worktrees root it was - as the worktree's directory, or in an orchestrator's - where list-worktree warns of each.
    /// </summary>
    [Fact]
    public async Task FormerWorkers_BesideAWorktreeDeletedSince_AreRemoved()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var root = (await HostDoubles.Loader(config, temp.Path).LoadAsync(temp.Path, TestContext.Current.CancellationToken)).Layout.WorktreesDirectoryUnder(config.Worktrees.Root);
        var plain = Path.Combine(root, "gone" + MutationWorkers.FormerFamily + "357e24cw-1");
        var agents = Path.Combine(root, "o1", "xa" + MutationWorkers.FormerFamily + "357e24cs-2");

        // All an unfinished removal of one left, with nothing else beside the tree it was kept beside.
        var aside = RemovalAside.Of(Path.Combine(root, "left" + MutationWorkers.FormerFamily + "357e24cw-3"));

        foreach (var made in new[] { plain, agents })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(made)!);
            await SyncKit.Transport(harness).CreateRootAsync(made, CopyMark.Complete, TestContext.Current.CancellationToken);
        }

        File.WriteAllText(Written(Path.Combine(aside, "part.o")), "what a removal left");

        var (outcome, leg) = await CleanAsync(temp, harness, config, formerWorkers: true);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.StartsWith("removed 3 worker(s) an earlier version kept beside its trees, ", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(plain));
        Assert.False(Directory.Exists(agents));
        Assert.False(Directory.Exists(aside));
    }

    /// <summary>
    /// A host asked removes the workers an earlier version kept beside its copy of the main checkout, and beside a copy of
    /// a worktree removed since, and nothing beside another repository's copy - measuring them first where asked to,
    /// removing nothing - here as through its agent.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormerWorkers_OnAHost_AreRemovedBesideItsCopies_ThereStillOrRemovedSince(bool throughAnAgent)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        ISyncTransport transport = throughAnAgent ? SyncKit.AgentHere(harness) : SyncKit.Transport(harness);
        var mine = temp.Path + MutationWorkers.FormerFamily + "357e24cw-1";
        var removedCopys = temp.Path + HostCopies.WorktreeSuffix + "gone" + MutationWorkers.FormerFamily + "357e24cs-1";
        var anothers = temp.Path + "-other" + MutationWorkers.FormerFamily + "357e24cw-1";
        var noCopys = temp.Path + HostCopies.WorktreeSuffix + "Not_A_Copy" + MutationWorkers.FormerFamily + "357e24cw-1";

        try
        {
            foreach (var made in new[] { mine, removedCopys, anothers, noCopys })
            {
                await SyncKit.Transport(harness).CreateRootAsync(made, CopyMark.Complete, TestContext.Current.CancellationToken);
            }

            var measured = await transport.RemoveFormerWorkersBesideCopiesAsync(temp.Path, measureOnly: true, TestContext.Current.CancellationToken);

            Assert.Equal([mine, removedCopys], measured.Removed.Select(worker => worker.Path).Order(StringComparer.Ordinal));
            Assert.True(Directory.Exists(mine));

            var removed = await transport.RemoveFormerWorkersBesideCopiesAsync(temp.Path, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, removed.Removed.Count);
            Assert.False(Directory.Exists(mine));
            Assert.False(Directory.Exists(removedCopys));
            Assert.True(Directory.Exists(anothers));
            Assert.True(Directory.Exists(noCopys));
            Assert.Empty((await transport.RemoveFormerWorkersBesideCopiesAsync(Path.Combine(temp.Path, "nowhere", "repo"), cancellationToken: TestContext.Current.CancellationToken)).Removed);
        }
        finally
        {
            foreach (var path in new[] { mine, removedCopys, anothers, noCopys }.Where(Directory.Exists))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    /// <summary>
    /// --former-workers looks beside a machine's trees once, whichever of the selected legs run there: the first leg on
    /// each machine removes them, every other leg there names it, and a host is asked once.
    /// </summary>
    [Fact]
    public async Task FormerWorkers_AreRemovedOnceAMachine_ByItsFirstLeg()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration(), ["release"] = new BuildConfiguration() },
            Hosts = OneHostLeg().Hosts,
            Legs =
            {
                ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug" },
                ["native-release"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "release" },
                ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = HostName },
                ["arm-release"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "release", Ssh = HostName },
            },
        };
        var former = temp.Path + MutationWorkers.FormerFamily + "357e24cw-1";
        var transport = Substitute.For<ISyncTransport>();
        var transports = Substitute.For<ISyncTransportFactory>();

        transport.RemoveFormerWorkersBesideCopiesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(WorkersRemoval.None);
        transports.For(Arg.Any<HostReport>()).Returns(transport);

        try
        {
            await SyncKit.Transport(harness).CreateRootAsync(former, CopyMark.Complete, TestContext.Current.CancellationToken);

            var (outcome, legs) = await CleanEveryLegAsync(temp, harness, config, OnTheHost(), transports: transports, formerWorkers: true);
            var said = legs.ToDictionary(leg => leg.GetProperty("leg").GetString()!, leg => leg.GetProperty("detail").GetString() ?? string.Empty, StringComparer.Ordinal);
            var here = new[] { "native", "native-release" };
            var removing = Assert.Single(here, leg => said[leg].StartsWith("removed 1 worker(s) an earlier version kept beside its trees, ", StringComparison.Ordinal));

            Assert.Equal(HarnessExit.Success, outcome.ExitCode);
            Assert.Equal(
                $"the workers an earlier version kept on this machine are cleaned with leg '{removing}'",
                said[Assert.Single(here, leg => leg != removing)]);
            Assert.False(Directory.Exists(former));
            await transport.Received(1).RemoveFormerWorkersBesideCopiesAsync(HostTree, false, Arg.Any<CancellationToken>());
            Assert.Single(["arm", "arm-release"], leg => said[leg].StartsWith("the workers an earlier version kept on ", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(former))
            {
                Directory.Delete(former, recursive: true);
            }
        }
    }

    /// <summary>
    /// The mutation workers a leg's sweeps keep in its tree are removed with its build directory, each named, and
    /// what they held counted; a claim a dead sweep left on one is released, and said, with it. Another leg's workers,
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

        config.Sanitizers["asan"] = new VariantOverlay();
        config.Legs["native-asan"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug", Sanitizer = "asan" };

        var first = workers.Made(variant, 1, 1000);
        var second = workers.Made(variant, 2, 24);
        var others = workers.Made(variant with { Sanitizer = "asan" }, 1, 10);
        var othersSelfTest = workers.Made(variant with { Sanitizer = "asan" }, 1, 10, selfTest: true);
        var aside = workers.Aside(variant, 3, 100);
        var othersAside = workers.Aside(variant with { Sanitizer = "asan" }, 2, 100);
        var anotherTreesAside = workers.AnotherTreesAside(variant, 4, 100);

        File.WriteAllText(
            first + MutationWorkers.ClaimSuffix,
            "{ \"machine\": \"" + Environment.MachineName + "\", \"processId\": " + (int.MaxValue - 1).ToString(CultureInfo.InvariantCulture)
            + ", \"processStamp\": \"gone\", \"runId\": \"20250101-120000-deadbeef\", \"takenUtc\": \"2025-01-01T12:00:00Z\" }");

        var (outcome, leg) = await CleanAsync(temp, harness, config, legs: ["native"]);

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
        Assert.True(Directory.Exists(othersSelfTest), "another leg's self-test's workers are that leg's");
        Assert.True(Directory.Exists(othersAside));
        Assert.True(Directory.Exists(anotherTreesAside), "a tree whose name starts alike and goes on is another tree");

        // What the workers held is the leg's to say as data too, and the room is measured once they are gone.
        Assert.Equal(Workers.Holding(1000) + Workers.Holding(24) + 100, leg.GetProperty("space").GetProperty("workerBytes").GetInt64());
    }

    /// <summary>
    /// A clean of a leg removes its build directory and its workers, and leaves the record of its verdicts as it was: it
    /// is in the main checkout's harness directory, outside every build directory and worker, and a worker made again
    /// after a clean comes to the same keys.
    /// </summary>
    [Fact]
    public async Task ACleanOfTheLeg_LeavesTheRecordAsItWas()
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var variant = Variant(harness, config);
        var worker = workers.Made(variant, 1, 1000);
        var recorded = ArmVerdictRecordTests.Recorded(harness.FileSystem, new HarnessLayout(temp.Path, temp.Path));

        var (outcome, _) = await CleanAsync(temp, harness, config, legs: ["native"]);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.False(Directory.Exists(worker));
        Assert.Equal(recorded, ArmVerdictRecordTests.Snapshot(new HarnessLayout(temp.Path, temp.Path)));
    }

    /// <summary>
    /// A clean of a leg takes its self-test's workers with its own, and those of a variant no leg of its host and tree
    /// builds any more - which nothing else would ever remove - each family under the lock a sweep of its variant takes,
    /// with what an earlier removal of them left aside.
    /// </summary>
    [Fact]
    public async Task ALegsClean_TakesItsSelfTestsWorkers_AndThoseOfAVariantNoLegBuildsAnyMore()
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var variant = Variant(harness, config);
        var renamed = variant with { Config = "relwithdebinfo" };
        var own = workers.Made(variant, 1, 10);
        var selfTest = workers.Made(variant, 1, 20, selfTest: true);
        var orphan = workers.Made(renamed, 2, 30);
        var orphanSelfTest = workers.Made(renamed, 1, 40, selfTest: true);
        var orphanAside = workers.Aside(renamed, 3, 50);
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var sweep = RunId.New();

        // Listed by the name each family's workers are kept under, as the disk sorts them.
        var held = new Dictionary<string, int> { [own] = 10, [selfTest] = 20, [orphan] = 30, [orphanSelfTest] = 40 };
        IReadOnlyList<string> Sorted(params string[] paths) => [.. paths.OrderBy(Path.GetFileName, StringComparer.Ordinal)];

        var (measured, dry) = await CleanAsync(temp, harness, config, dryRun: true);

        Assert.Equal(HarnessExit.Success, measured.ExitCode);
        Assert.EndsWith(
            "; 4 mutation worker(s): "
            + string.Join(", ", Sorted(own, selfTest, orphan, orphanSelfTest).Select(path => $"{DiskSpace.Size(Workers.Holding(held[path]))} in '{path}'"))
            + $", {DiskSpace.Size(50)} a removal that did not finish left aside",
            dry.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(Workers.Holding(10) + Workers.Holding(20) + Workers.Holding(30) + Workers.Holding(40) + 50, dry.GetProperty("space").GetProperty("workerBytes").GetInt64());
        Assert.All(new[] { own, selfTest, orphan, orphanSelfTest, orphanAside }, path => Assert.True(Directory.Exists(path), path));

        // A sweep of the variant no leg builds any more - one still running from before the configuration changed -
        // keeps that variant's workers alone; the leg's own, and its self-test's, go.
        await using (await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            MutationWorkers.SweepLock(HostId.Local, temp.Path, renamed, sweep, MutationService.CommandName),
            TestContext.Current.CancellationToken))
        {
            var (_, locked) = await CleanAsync(temp, harness, config, runLock: runLock);
            var said = locked.GetProperty("detail").GetString()!;

            Assert.Equal("refused-locked", locked.GetProperty("verdict").GetString());
            Assert.Contains(
                $"removed 2 mutation worker(s), {DiskSpace.Size(Workers.Holding(10) + Workers.Holding(20) + 50)} with what an earlier removal of them left aside: "
                + string.Join(", ", Sorted(own, selfTest).Select(path => $"'{path}'")),
                said,
                StringComparison.Ordinal);
            Assert.Contains($"the mutation workers keyed '{renamed.CopyKey}', of a variant no leg here builds, were left, as a sweep holds them: ", said, StringComparison.Ordinal);
            Assert.Contains(sweep.Value, said, StringComparison.Ordinal);
            Assert.All(new[] { own, selfTest, orphanAside }, path => Assert.False(Directory.Exists(path), path));
            Assert.All(new[] { orphan, orphanSelfTest }, path => Assert.True(Directory.Exists(path), path));
        }

        var (outcome, leg) = await CleanAsync(temp, harness, config, runLock: runLock);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.EndsWith(
            $"; removed 2 mutation worker(s), {DiskSpace.Size(Workers.Holding(30) + Workers.Holding(40))}: " + string.Join(", ", Sorted(orphan, orphanSelfTest).Select(path => $"'{path}'")),
            leg.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.All(new[] { orphan, orphanSelfTest }, path => Assert.False(Directory.Exists(path), path));
    }

    /// <summary>
    /// A variant only legs elsewhere build - on a host, in a distribution, or bound to another worktree - keeps its
    /// workers in its own tree there: ones of its name in this tree are no leg's here, and go with a clean of
    /// a leg that is.
    /// </summary>
    [Fact]
    public async Task WorkersOfAVariantOnlyLegsElsewhereBuild_AreNoLegsHere_AndGoWithAClean()
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var os = harness.Platform.PlatformKey;
        var processor = harness.Platform.Processor;
        var config = new HarnessConfig
        {
            BuildConfigs =
            {
                ["debug"] = new BuildConfiguration(),
                ["release"] = new BuildConfiguration(),
                ["relwithdebinfo"] = new BuildConfiguration(),
                ["minsizerel"] = new BuildConfiguration(),
            },
            Hosts = new HostsConfig
            {
                Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree } },
                Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = HostTree } },
            },
            Legs =
            {
                ["native"] = HostDoubles.Leg(os, processor),
                ["on-host"] = new LegConfig { Os = os, Processor = processor, Config = "release", Ssh = HostName },
                ["in-wsl"] = new LegConfig { Os = os, Processor = processor, Config = "relwithdebinfo", Wsl = "Ubuntu" },
                ["bound"] = new LegConfig { Os = os, Processor = processor, Config = "minsizerel", Worktree = "other" },
            },
        };

        var variant = VariantKey.For(config, config.Legs["native"], os);
        var onHost = workers.Made(variant with { Config = "release" }, 1, 10);
        var inWsl = workers.Made(variant with { Config = "relwithdebinfo" }, 1, 20);
        var bound = workers.Made(variant with { Config = "minsizerel" }, 1, 30);

        var (outcome, leg) = await CleanAsync(temp, harness, config, legs: ["native"]);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Contains("removed 3 mutation worker(s), ", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.All(new[] { onHost, inWsl, bound }, path => Assert.False(Directory.Exists(path), path));
    }

    /// <summary>
    /// A sweep of the leg holds its self-test's workers with its own, under the one lock: a clean leaves both and says
    /// so once, as the leg's, never as those of a variant no leg builds.
    /// </summary>
    [Fact]
    public async Task ASweepOfTheLeg_HoldsItsSelfTestsWorkersWithItsOwn_SaidOnce()
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var variant = Variant(harness, config);
        var own = workers.Made(variant, 1, 10);
        var selfTest = workers.Made(variant, 1, 20, selfTest: true);
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);

        await using (await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            MutationWorkers.SweepLock(HostId.Local, MutationWorkers.Of(temp.Path, variant, selfTest: true), RunId.New(), MutationService.CommandName),
            TestContext.Current.CancellationToken))
        {
            var (_, locked) = await CleanAsync(temp, harness, config, runLock: runLock);
            var said = locked.GetProperty("detail").GetString()!;

            Assert.Equal("refused-locked", locked.GetProperty("verdict").GetString());
            Assert.Single(said.Split("its mutation workers were left, as a sweep of the leg holds them: ", StringSplitOptions.None).Skip(1));
            Assert.DoesNotContain("which no leg here builds", said, StringComparison.Ordinal);
            Assert.DoesNotContain("removed", said.Split(';', 2)[1], StringComparison.Ordinal);
            Assert.All(new[] { own, selfTest }, path => Assert.True(Directory.Exists(path), path));
        }
    }

    /// <summary>
    /// A worker gone by the time its variant's lock is held - another leg of the same command, cleaning the same tree,
    /// took one no leg builds - is passed over: the clean removes what is there, and fails for nothing.
    /// </summary>
    [Fact]
    public async Task AWorkerTakenBeforeItsLockIsHeld_IsPassedOver()
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var variant = Variant(harness, config);
        var first = workers.Made(variant, 1, 10);
        var second = workers.Made(variant, 2, 20);

        var (outcome, leg) = await CleanAsync(temp, harness, config, fileSystem: new TakenMeanwhile(harness.FileSystem, moved: first, taken: second));

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("passed", leg.GetProperty("verdict").GetString());
        Assert.EndsWith(
            $"; removed 1 mutation worker(s), {DiskSpace.Size(Workers.Holding(10))}: '{first}'",
            leg.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(Workers.Holding(10), leg.GetProperty("space").GetProperty("workerBytes").GetInt64());
    }

    /// <summary>The disk, on which one directory is gone once another has been moved: taken by somebody else meanwhile.</summary>
    private sealed class TakenMeanwhile(IFileSystem inner, string moved, string taken) : PassThroughFileSystem(inner)
    {
        public override void MoveDirectory(string source, string destination)
        {
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(moved), StringComparison.OrdinalIgnoreCase))
            {
                base.DeleteDirectory(taken);
            }

            base.MoveDirectory(source, destination);
        }
    }

    /// <summary>
    /// A leg whose mutation workers cannot be listed - the directory they are kept in will not be read - fails its
    /// clean, saying why: never passed over as a leg that keeps none.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkersThatCannotBeListed_FailTheLegsClean_SayingWhy(bool denied)
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var worker = workers.Made(Variant(harness, config), 1, 10);

        var (outcome, leg) = await CleanAsync(temp, harness, config, fileSystem: new CannotList(harness.FileSystem, Path.Combine(temp.Path, HarnessLayout.MutationsDirectoryName), denied));

        Assert.Equal("failed", leg.GetProperty("verdict").GetString());
        Assert.EndsWith("; its mutation workers could not be listed: the disk would not say", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(Verdicts.ExitCodeFor(LegVerdict.Failed), outcome.ExitCode);
        Assert.True(Directory.Exists(worker));
    }

    /// <summary>
    /// A dry run that cannot measure what a leg's workers hold - what a removal left aside will not be weighed - fails
    /// the leg, saying why: never passed as a leg whose workers were measured, with nothing said of them.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkersThatCannotBeMeasured_FailTheLegsDryRun_SayingWhy(bool denied)
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var aside = workers.Aside(Variant(harness, config), 1, 10);

        var (outcome, leg) = await CleanAsync(temp, harness, config, dryRun: true, fileSystem: new CannotWeigh(harness.FileSystem, aside, denied));

        Assert.Equal("failed", leg.GetProperty("verdict").GetString());
        Assert.EndsWith("; its mutation workers could not be measured: the disk would not say", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(Verdicts.ExitCodeFor(LegVerdict.Failed), outcome.ExitCode);
        Assert.Equal(0, leg.GetProperty("space").GetProperty("workerBytes").GetInt64());
        Assert.True(Directory.Exists(aside));
    }

    /// <summary>A disk that will not weigh one directory: something holds it, or it is not this user's to read.</summary>
    private sealed class CannotWeigh(IFileSystem inner, string directory, bool denied) : PassThroughFileSystem(inner)
    {
        public override long DirectorySize(string path)
            => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) ? base.DirectorySize(path)
                : denied ? throw new UnauthorizedAccessException("the disk would not say.")
                : throw new IOException("the disk would not say.");
    }

    /// <summary>
    /// A leg whose mutation workers cannot all be removed fails its clean, saying why, and counts only what went: the
    /// one left is aside by then, where the next clean of the leg removes it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkersThatCannotAllBeRemoved_FailTheLegsClean_CountingOnlyWhatWent(bool denied)
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var variant = Variant(harness, config);
        var first = workers.Made(variant, 1, 10);
        var second = workers.Made(variant, 2, 20);

        var (outcome, leg) = await CleanAsync(temp, harness, config, fileSystem: new CannotRemove(harness.FileSystem, Workers.AsideOf(second), denied));

        Assert.Equal("failed", leg.GetProperty("verdict").GetString());
        Assert.EndsWith(
            $"; its mutation workers could not all be removed: the disk would not let go; removed 1 mutation worker(s), {DiskSpace.Size(Workers.Holding(10))}: '{first}'; "
            + "what is left of any moved aside is removed by the next clean of this leg",
            leg.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(Verdicts.ExitCodeFor(LegVerdict.Failed), outcome.ExitCode);
        Assert.Equal(Workers.Holding(10), leg.GetProperty("space").GetProperty("workerBytes").GetInt64());
        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(Workers.AsideOf(first)));
        Assert.True(Directory.Exists(Workers.AsideOf(second)));

        // Still held, it is tried again by the next clean, which counts nothing of it while it cannot remove it.
        var (_, held) = await CleanAsync(temp, harness, config, fileSystem: new CannotRemove(harness.FileSystem, Workers.AsideOf(second), denied));

        Assert.Equal("failed", held.GetProperty("verdict").GetString());
        Assert.EndsWith(
            "; its mutation workers could not all be removed: the disk would not let go; what is left of any moved aside is removed by the next clean of this leg",
            held.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(0, held.GetProperty("space").GetProperty("workerBytes").GetInt64());
        Assert.True(Directory.Exists(Workers.AsideOf(second)));

        var (again, next) = await CleanAsync(temp, harness, config);

        Assert.Equal(HarnessExit.Success, again.ExitCode);
        Assert.Equal(Workers.Holding(20), next.GetProperty("space").GetProperty("workerBytes").GetInt64());
        Assert.False(Directory.Exists(Workers.AsideOf(second)));
    }

    /// <summary>A disk that will not remove one directory: something holds it, or it is not this user's to remove.</summary>
    private sealed class CannotRemove(IFileSystem inner, string directory, bool denied) : PassThroughFileSystem(inner)
    {
        public override void DeleteDirectory(string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            {
                throw denied ? new UnauthorizedAccessException("the disk would not let go.") : new IOException("the disk would not let go.");
            }

            base.DeleteDirectory(path);
        }
    }

    /// <summary>
    /// The room a leg's line says is the room once its workers are gone too: they go before its build directory, whose
    /// removal measures it.
    /// </summary>
    [Fact]
    public async Task TheRoomALegSays_IsMeasuredOnceItsWorkersAreGone()
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var worker = workers.Made(Variant(harness, config), 1, 10);

        File.WriteAllText(Written(Path.Combine(BuildDirectory(temp, harness, config), "a.o")), "object");

        var (outcome, leg) = await CleanAsync(temp, harness, config, fileSystem: new RoomWithout(harness.FileSystem, worker));

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(RoomWithout.Freed, leg.GetProperty("space").GetProperty("disk").GetProperty("freeBytes").GetInt64());
    }

    /// <summary>A disk whose room is <see cref="Freed"/> once one directory is gone, and <see cref="Held"/> until then.</summary>
    private sealed class RoomWithout(IFileSystem inner, string directory) : PassThroughFileSystem(inner)
    {
        public const long Held = 1L << 30;

        public const long Freed = 5L << 30;

        public override DiskSpace SpaceAt(string path) => new(DirectoryExists(directory) ? Held : Freed, 8L << 30, "/");
    }

    /// <summary>
    /// A leg whose build directory is not measured - a link, left alone - still carries what was removed of its mutation
    /// workers: its space says them alone, and nothing of the directory.
    /// </summary>
    [Fact]
    public async Task ALegWhoseBuildDirectoryIsNotMeasured_StillCarriesWhatWasRemovedOfItsWorkers()
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var directory = BuildDirectory(temp, harness, config);
        var elsewhere = temp.WriteFile(Path.Combine("elsewhere", "a.o"), "object");
        var worker = workers.Made(Variant(harness, config), 1, 10);

        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        TestLinks.OrSkip(() => Directory.CreateSymbolicLink(directory, Path.GetDirectoryName(elsewhere)!));

        var (_, leg) = await CleanAsync(temp, harness, config);
        var space = leg.GetProperty("space");

        Assert.Equal("failed", leg.GetProperty("verdict").GetString());
        Assert.False(Directory.Exists(worker));
        Assert.Equal(Workers.Holding(10), space.GetProperty("workerBytes").GetInt64());
        Assert.Equal((0, false), (space.GetProperty("buildBytes").GetInt64(), space.GetProperty("removed").GetBoolean()));
        Assert.False(space.TryGetProperty("disk", out var disk) && disk.ValueKind != JsonValueKind.Null);
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

    /// <summary>
    /// The copies of its tree a leg's legs work in are removed with its build directory - its own variant's, and those of
    /// a variant no leg builds any more, whatever the configuration says now: a copy made under a setting since changed
    /// is still the leg's. A dry run measures each; a copy a run holds is left, saying who, and that leg's clean is
    /// refused-locked.
    /// </summary>
    [Fact]
    public async Task TheCopiesOfItsTreeALegsLegsWorkIn_AreRemovedWithItsBuildDirectory()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var variant = Variant(harness, config);
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var own = Copy(temp, variant, 100);
        var orphan = Copy(temp, variant with { Config = "relwithdebinfo" }, 40);

        // Listed by the name each copy is kept under, as the disk sorts them: the keys, which hash the processor too,
        // put either first.
        var holds = new Dictionary<string, int> { [own] = 100, [orphan] = 40 };
        var (measured, dry) = await CleanAsync(temp, harness, config, dryRun: true);

        Assert.Equal(HarnessExit.Success, measured.ExitCode);
        Assert.EndsWith(
            "; 2 leg copy(ies): "
            + string.Join(", ", new[] { own, orphan }.OrderBy(Path.GetFileName, StringComparer.Ordinal).Select(path => $"{DiskSpace.Size(Workers.Holding(holds[path]))} in '{path}'")),
            dry.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(Workers.Holding(100) + Workers.Holding(40), dry.GetProperty("space").GetProperty("copyBytes").GetInt64());
        Assert.All(new[] { own, orphan }, path => Assert.True(Directory.Exists(path), path));

        // A run building in one keeps it alone; the copy of the variant no leg builds any more still goes.
        await using (var held = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            PlacedLeg.BuildLock(HostId.Local, own, variant, RunId.New(), "test"),
            TestContext.Current.CancellationToken))
        {
            var (_, locked) = await CleanAsync(temp, harness, config, runLock: runLock);
            var detail = locked.GetProperty("detail").GetString()!;

            Assert.Equal("refused-locked", locked.GetProperty("verdict").GetString());
            Assert.Contains($"its copy '{own}' was left, as a run holds it: ", detail);
            Assert.Contains(held.Entry.Holder.RunId, detail, StringComparison.Ordinal);
            Assert.Equal(Workers.Holding(40), locked.GetProperty("space").GetProperty("copyBytes").GetInt64());
            Assert.True(Directory.Exists(own));
            Assert.False(Directory.Exists(orphan));
        }

        var (again, free) = await CleanAsync(temp, harness, config, runLock: runLock);

        Assert.Equal(HarnessExit.Success, again.ExitCode);
        Assert.EndsWith($"; removed 1 leg copy(ies), {DiskSpace.Size(Workers.Holding(100))}: '{own}'", free.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(own));
    }

    /// <summary>
    /// A copy its legs work in that a run holds under the copy's own lock - a command another machine sent here, run in
    /// the copy it was sent: a sweep in a copy of its own - is left by a clean, saying who holds it, as one a run of the
    /// tree's own holds is.
    /// </summary>
    [Fact]
    public async Task ACopyARunHoldsUnderTheCopysOwnLock_IsLeftByAClean()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var variant = Variant(harness, config);
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var own = Copy(temp, variant, 100);

        await using var held = await runLock.AcquireAsync(
            new HarnessLayout(own, own),
            MutationWorkers.SweepLock(HostId.Local, own, variant, RunId.New(), MutationService.CommandName),
            TestContext.Current.CancellationToken);

        var (_, leg) = await CleanAsync(temp, harness, config, runLock: runLock);
        var detail = leg.GetProperty("detail").GetString()!;

        Assert.Equal("refused-locked", leg.GetProperty("verdict").GetString());
        Assert.Contains($"its copy '{own}' was left, as a run holds it: ", detail, StringComparison.Ordinal);
        Assert.Contains(held.Entry.Holder.RunId, detail, StringComparison.Ordinal);
        Assert.True(Directory.Exists(own));
    }

    /// <summary>
    /// A directory under the name of a copy its legs work in that no sync made is somebody's: a clean leaves it, and what
    /// is in it, and says so - as a dry run says the clean would.
    /// </summary>
    [Fact]
    public async Task ADirectoryUnderALegCopysNameThatNoSyncMade_IsLeftByAClean_AndSaidSo()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var somebodys = LegCopies.PathOf(temp.Path, Variant(harness, config));
        var file = Written(Path.Combine(somebodys, "mine", "a.c"));
        const string Unmarked = "nothing there says the harness made it, so it is yours to remove";

        File.WriteAllText(file, "int mine;");

        var (_, dry) = await CleanAsync(temp, harness, config, dryRun: true);

        Assert.EndsWith(
            $"; 1 leg copy(ies): {DiskSpace.Size(9)} in '{somebodys}', which a clean leaves: {Unmarked}",
            dry.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        var (outcome, leg) = await CleanAsync(temp, harness, config);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.EndsWith(
            $"; '{somebodys}' is named as a copy its legs work in, and was left: {Unmarked}",
            leg.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(0, leg.GetProperty("space").GetProperty("copyBytes").GetInt64());
        Assert.Equal("int mine;", File.ReadAllText(file));
    }

    /// <summary>
    /// A dry run of a clean ends as the clean would: a leg a run is building is refused-locked in the dry run too, exit
    /// 13, the run named - with what the directory holds still said, and still in its space, and nothing removed or
    /// moved aside.
    /// </summary>
    [Fact]
    public async Task ADryRunOfAClean_EndsRefusedLockedForALegARunIsBuilding_WithWhatItHoldsStillSaid()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var directory = BuildDirectory(temp, harness, config);
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);

        File.WriteAllText(Written(Path.Combine(directory, "obj", "a.o")), new string('a', 1000));

        await using (var held = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            PlacedLeg.BuildLock(HostId.Local, temp.Path, Variant(harness, config), RunId.New(), "build"),
            TestContext.Current.CancellationToken))
        {
            var (outcome, leg) = await CleanAsync(temp, harness, config, runLock: runLock, dryRun: true);
            var detail = leg.GetProperty("detail").GetString()!;

            Assert.Equal("refused-locked", leg.GetProperty("verdict").GetString());
            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.StartsWith($"'{temp.Path}' variant '{Variant(harness, config).DirectoryName}' on local is held by ", detail);
            Assert.Contains($"1000 bytes in '{directory}'", detail);
            Assert.False(detail.EndsWith('.'), "the room ends the detail as it ends a passed one, so a part joined after it reads on: " + detail);
            Assert.Equal(1000, leg.GetProperty("space").GetProperty("buildBytes").GetInt64());
            Assert.True(Directory.Exists(directory), "a dry run removes nothing");
        }

        var (after, freed) = await CleanAsync(temp, harness, config, runLock: runLock, dryRun: true);

        Assert.Equal(HarnessExit.Success, after.ExitCode);
        Assert.Equal("passed", freed.GetProperty("verdict").GetString());
        Assert.StartsWith($"1000 bytes in '{directory}'", freed.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.True(Directory.Exists(directory));
    }

    /// <summary>
    /// A dry run of a clean of a leg on a host ends as the clean would: a run of this machine building the leg there
    /// holds this machine's lock for it, so the dry run is refused-locked, the holder named - and the host is sent
    /// nothing, nor its copy asked.
    /// </summary>
    [Fact]
    public async Task ADryRunOfACleanOfALegOnAHostARunOfThisMachineIsBuilding_IsRefusedLocked_AndTheHostIsSentNothing()
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

        var (outcome, leg) = await CleanAsync(temp, harness, OneHostLeg(), OnTheHost(), runLock, hosts, transports: transports, dryRun: true);

        Assert.Equal(Verdicts.ExitCodeFor(LegVerdict.RefusedLocked), outcome.ExitCode);
        Assert.Equal("refused-locked", leg.GetProperty("verdict").GetString());
        Assert.Contains(held.Entry.Holder.RunId, leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(hosts.Calls);
        transports.DidNotReceiveWithAnyArgs().For(default!);
    }

    /// <summary>
    /// A dry run of a clean whose leg's mutation workers a sweep holds ends as the clean would: refused-locked, the
    /// workers said as the clean says them, with what they hold still measured - and nothing removed.
    /// </summary>
    [Fact]
    public async Task ADryRunOfAClean_WhoseWorkersASweepHolds_IsRefusedLocked_WithEachStillMeasured()
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var variant = Variant(harness, config);
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var held = workers.Made(variant, 1, 1000);
        var sweep = RunId.New();

        await using var taken = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            MutationWorkers.SweepLock(HostId.Local, MutationWorkers.Of(temp.Path, variant), sweep, "check-mutations"),
            TestContext.Current.CancellationToken);

        var (outcome, leg) = await CleanAsync(temp, harness, config, runLock: runLock, dryRun: true);
        var detail = leg.GetProperty("detail").GetString()!;

        Assert.Equal(Verdicts.ExitCodeFor(LegVerdict.RefusedLocked), outcome.ExitCode);
        Assert.Equal("refused-locked", leg.GetProperty("verdict").GetString());
        Assert.Contains($"its mutation workers were left, as a sweep of the leg holds them: ", detail);
        Assert.Contains($"run {sweep.Value}", detail, StringComparison.Ordinal);
        Assert.Contains($"1 mutation worker(s): {DiskSpace.Size(Workers.Holding(1000))} in '{held}'", detail, StringComparison.Ordinal);
        Assert.Equal(Workers.Holding(1000), leg.GetProperty("space").GetProperty("workerBytes").GetInt64());
        Assert.True(Directory.Exists(held), "a dry run removes nothing");
    }

    /// <summary>
    /// A dry run of a clean one of whose workers a sweep still running claims ends as the clean would: refused-locked, the
    /// worker and the run claiming it named as the clean names them, each worker still measured - and nothing removed.
    /// </summary>
    [Fact]
    public async Task ADryRunOfAClean_OneOfWhoseWorkersALiveSweepClaims_IsRefusedLocked_AsTheCleanIs()
    {
        using var temp = new TempDirectory();
        using var workers = new Workers(temp);
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var variant = Variant(harness, config);
        var free = workers.Made(variant, 1, 10);
        var claimed = workers.Made(variant, 2, 20);
        var claim = RunId.New();

        new DirectoryClaims(harness.FileSystem, harness.Output, harness.Identity, MutationWorkers.Claims(MutationService.CommandName)).Claim(claimed, claim, force: false);

        var (dry, measured) = await CleanAsync(temp, harness, config, dryRun: true);
        var detail = measured.GetProperty("detail").GetString()!;

        Assert.Equal(Verdicts.ExitCodeFor(LegVerdict.RefusedLocked), dry.ExitCode);
        Assert.Equal("refused-locked", measured.GetProperty("verdict").GetString());
        Assert.Contains($"worker 2, '{claimed}', is claimed by a sweep still running: ", detail, StringComparison.Ordinal);
        Assert.Contains(claim.Value, detail, StringComparison.Ordinal);
        Assert.Contains(
            $"2 mutation worker(s): {DiskSpace.Size(Workers.Holding(10))} in '{free}', {DiskSpace.Size(Workers.Holding(20))} in '{claimed}'",
            detail,
            StringComparison.Ordinal);
        Assert.All(new[] { free, claimed }, path => Assert.True(Directory.Exists(path), path));

        // The clean it stands for ends the same way.
        var (cleaned, _) = await CleanAsync(temp, harness, config);

        Assert.Equal(dry.ExitCode, cleaned.ExitCode);
    }

    /// <summary>
    /// A dry run of a clean holding one of the leg's copies ends as the clean would: refused-locked, the copy said as
    /// left and who holds it, both copies' sizes still in its space - and nothing removed.
    /// </summary>
    [Fact]
    public async Task ADryRunOfAClean_HoldingALegCopy_IsRefusedLocked_WithEachStillMeasured()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var variant = Variant(harness, config);
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var own = Copy(temp, variant, 100);
        var orphan = Copy(temp, variant with { Config = "relwithdebinfo" }, 40);

        await using var held = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            PlacedLeg.BuildLock(HostId.Local, own, variant, RunId.New(), "test"),
            TestContext.Current.CancellationToken);

        var (outcome, leg) = await CleanAsync(temp, harness, config, runLock: runLock, dryRun: true);
        var detail = leg.GetProperty("detail").GetString()!;

        Assert.Equal(Verdicts.ExitCodeFor(LegVerdict.RefusedLocked), outcome.ExitCode);
        Assert.Equal("refused-locked", leg.GetProperty("verdict").GetString());
        Assert.Contains($"its copy '{own}' was left, as a run holds it: ", detail);
        Assert.Contains(held.Entry.Holder.RunId, detail, StringComparison.Ordinal);
        Assert.Equal(Workers.Holding(100) + Workers.Holding(40), leg.GetProperty("space").GetProperty("copyBytes").GetInt64());
        Assert.All(new[] { own, orphan }, path => Assert.True(Directory.Exists(path), path));
    }

    /// <summary>A copy of the tree its legs work in, marked as a sync makes one, its build holding <paramref name="bytes"/>.</summary>
    private static string Copy(TempDirectory temp, VariantKey variant, int bytes)
    {
        var copy = LegCopies.PathOf(temp.Path, variant);

        Directory.CreateDirectory(Path.Combine(copy, HarnessLayout.DirectoryName));
        File.WriteAllText(
            Path.Combine(copy, HarnessLayout.DirectoryName, HarnessLayout.SyncedCopyMarkerName),
            """{"CreatedUtc":"2026-10-07T12:00:00Z","CreatedBy":"builder","Adopted":false,"Completed":true}""");
        File.WriteAllText(Written(Path.Combine(variant.DirectoryUnder(copy), "a.o")), new string('a', bytes));

        return copy;
    }

    /// <summary>The variant the one leg of <paramref name="config"/> builds on this machine.</summary>
    private static VariantKey Variant(HarnessFactory harness, HarnessConfig config)
        => VariantKey.For(config, config.Legs.Single().Value, harness.Platform.PlatformKey);

    /// <summary>
    /// Mutation workers in a test's tree, in its slot for them, removed once the test is done, with whatever a removal
    /// left aside.
    /// </summary>
    private sealed class Workers(TempDirectory temp) : IDisposable
    {
        /// <summary>The marker a sync leaves in a copy it made.</summary>
        private const string Marker = """{"CreatedUtc":"2026-10-07T12:00:00Z","CreatedBy":"builder","Adopted":false,"Completed":true}""";

        private readonly List<string> _made = [];

        /// <summary>What a worker made holding <paramref name="bytes"/> in its build comes to, its marker with it.</summary>
        public static long Holding(long bytes) => bytes + Marker.Length;

        /// <summary>
        /// Worker <paramref name="number"/> of <paramref name="variant"/> - or of its self-test - as a sync makes one, its
        /// build holding <paramref name="bytes"/>.
        /// </summary>
        public string Made(VariantKey variant, int number, int bytes, bool selfTest = false)
        {
            var worker = Track(MutationWorkers.Of(temp.Path, variant, selfTest).PathOf(number));

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
        public string Aside(VariantKey variant, int number, int bytes) => Aside(temp.Path, variant, number, bytes);

        /// <summary>
        /// What an earlier removal left aside of worker <paramref name="number"/> of another tree beside this one, in that
        /// tree's own slot, whose name starts as this tree's does and goes on.
        /// </summary>
        public string AnotherTreesAside(VariantKey variant, int number, int bytes) => Aside(temp.Path + "x", variant, number, bytes);

        private string Aside(string tree, VariantKey variant, int number, int bytes)
        {
            var aside = Track(AsideOf(MutationWorkers.PathOf(tree, variant, number)));

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
    private static RecordingInspector OnTheHost() => new(host => host.Kind == HostKind.Local
        ? new HostReport { Host = host, Os = new HarnessFactory().Platform.PlatformKey, Processor = new HarnessFactory().Platform.Processor }
        : new HostReport
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
        ISyncTransportFactory? transports = null,
        IReadOnlyList<string>? legs = null,
        IFileSystem? fileSystem = null,
        IRepositoryTrees? trees = null,
        bool formerWorkers = false,
        string? mainCheckout = null,
        CancellationToken? cancellationToken = null,
        HostId? here = null)
    {
        var (outcome, cleaned) = await CleanEveryLegAsync(
            temp, harness, config, inspector, runLock, hosts, dryRun, copyThere, transports, legs, fileSystem, trees, formerWorkers, mainCheckout, cancellationToken, here);

        return (outcome, Assert.Single(cleaned));
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
        ISyncTransportFactory? transports = null,
        IReadOnlyList<string>? legs = null,
        IFileSystem? fileSystem = null,
        IRepositoryTrees? trees = null,
        bool formerWorkers = false,
        string? mainCheckout = null,
        CancellationToken? cancellationToken = null,
        HostId? here = null)
    {
        var loader = HostDoubles.Loader(config, temp.Path, mainCheckout);

        if (transports is null)
        {
            var transport = Substitute.For<ISyncTransport>();
            transport.RootExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(copyThere);
            transport.ListCopiesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<HostCopyFound>());
            transport.RemoveWorkersAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(WorkersRemoval.None);
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
            }), new KnownTrees(), harness.Platform, harness.Output),
            runLock ?? new RunLock(harness.FileSystem, harness.Output, harness.Identity),
            transports,
            new RemoteLegRunner(hosts ?? new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command)), harness.Output),
            new LegExecutor(harness.Platform, harness.Output),
            SyncKit.Service(harness, loader),
            SyncKit.Transport(harness, fileSystem),
            harness.Identity,
            fileSystem ?? harness.FileSystem,
            harness.Platform,
            harness.Output,
            trees ?? new KnownTrees());

        var outcome = await service.RunAsync(
            new CleanRequest(temp.Path, legs, dryRun, Json: true, here) { FormerWorkers = formerWorkers },
            cancellationToken ?? TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));

        return (outcome, [.. document.RootElement.GetProperty("legs").EnumerateArray().Select(leg => leg.Clone())]);
    }
}
