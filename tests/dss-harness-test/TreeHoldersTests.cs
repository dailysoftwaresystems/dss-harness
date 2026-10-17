using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>
/// A dry run names who holds what its --apply takes, and exits as that --apply would: every holder, whether the dry run
/// is otherwise refused or not, said with where that run's leg stands, and the same words on the refusal an --apply
/// gives. The dry run asks the lock for exactly the trees its apply takes, and writes nothing.
/// </summary>
public sealed class TreeHoldersTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>An agent whose fold has something to write, so its dry run is otherwise sound.</summary>
    private static async Task<(OrchestrationKit Kit, string Worktree)> AFoldToMakeAsync(TempDirectory temp)
    {
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");

        return (kit, worktree);
    }

    /// <summary>Takes <paramref name="tree"/> whole for a run of <paramref name="command"/>, for as long as the caller holds the attempt.</summary>
    private static async Task<RunLockHandle> HoldAsync(OrchestrationKit kit, string tree, RunId? run = null, string command = "test")
    {
        var attempt = await kit.Harness.RunLock
            .TryAcquireAsync(new HarnessLayout(kit.Main, kit.Main), new LockRequest { Host = "local", Tree = tree, Scope = LockScope.TreeExclusive, RunId = run ?? RunId.New(), Command = command }, Token);

        return attempt.Handle ?? throw new InvalidOperationException($"the test could not hold '{tree}': {attempt.HeldBy}");
    }

    /// <summary>Takes <paramref name="tree"/> for one variant of a run of <paramref name="command"/>, as a leg working in a copy does.</summary>
    private static async Task<RunLockHandle> HoldSharedAsync(OrchestrationKit kit, string tree, RunId run, string variant = "debug", string command = "test")
    {
        var attempt = await kit.Harness.RunLock
            .TryAcquireAsync(
                new HarnessLayout(kit.Main, kit.Main),
                new LockRequest { Host = "local", Tree = tree, Variant = variant, Scope = LockScope.TreeShared, RunId = run, Command = command },
                Token)
            ;

        return attempt.Handle ?? throw new InvalidOperationException($"the test could not hold '{tree}': {attempt.HeldBy}");
    }

    /// <summary>Writes <paramref name="entries"/> as the record of heavy legs the kit's agents read.</summary>
    private static void WriteSlots(OrchestrationKit kit, params SlotEntry[] entries)
        => AdmissionKit.Write(kit.Harness.Slots.Location, entries);

    /// <summary>An entry of the heavy legs' record for the run holding <paramref name="tree"/> on <paramref name="host"/>, this process standing for it.</summary>
    private static SlotEntry InLine(OrchestrationKit kit, string tree, string run, string leg = "the-holder", int slots = 1, string host = "local")
        => new(kit.Harness.Identity.CurrentMachine, kit.Harness.Identity.CurrentId, run, DateTimeOffset.UtcNow, "test", leg, host, tree, slots, kit.Harness.Identity.Current);

    /// <summary>A leg's copy of the worktree at <paramref name="path"/>, as a sync makes one.</summary>
    private static async Task<string> LegCopyAsync(OrchestrationKit kit, string path)
    {
        await kit.Harness.LocalTransport.CreateRootAsync(path, CopyMark.Complete, Token);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        File.WriteAllText(Path.Combine(path, "main.c"), "int main;");

        return path;
    }

    /// <summary>
    /// A dry run of a fold whose request is sound names a run that holds the agent's worktree, exits 13, and its last
    /// line says both things a reader needs: that nothing in the request needs correcting, and that waiting is all there
    /// is to do. The run gone, the same dry run exits 0 and prints no holder.
    /// </summary>
    [Fact]
    public async Task ASoundDryRunOfAFoldNamesTheRunHoldingTheWorktree_AndIsRefusedUntilItHasEnded()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        await using var held = await HoldAsync(kit, worktree);

        var dry = await kit.FoldAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.StartsWith("dry run: folding agent 'ag' of 'o1' writes 1 path(s) into the main tree", dry.Message, StringComparison.Ordinal);
        Assert.EndsWith(
            "and nothing in this request needs correcting; --apply is refused now only because a run holds what it needs, named on the line(s) 'held now:' above: "
                + "run the same command again once that run has ended or is stopped",
            dry.Message,
            StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.StartsWith($"held now: '{worktree}' on local is held by ", StringComparison.Ordinal));
        Assert.Contains(dry.Details!, line => line.EndsWith(", running 'test'.", StringComparison.Ordinal));

        await held.DisposeAsync();

        var free = await kit.FoldAsync("ag", apply: false);

        Assert.True(free.Succeeded, OrchestrationKit.Describe(free));
        Assert.DoesNotContain("held now:", free.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(free.Details ?? [], line => line.StartsWith("held now:", StringComparison.Ordinal));    }

    /// <summary>
    /// A dry run refused for its own request names the run that holds the worktree too, and never says that nothing in
    /// the request needs correcting: its last line is the one clause that a run also holds what its --apply needs.
    /// </summary>
    [Fact]
    public async Task ADryRunRefusedForItsOwnRequestNamesTheRunToo_AndNeverSaysNothingNeedsCorrecting()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        await using var held = await HoldAsync(kit, worktree);

        var dry = await kit.FoldAsync("ag", apply: false, new FoldAllowances { Merge = ["src/typo.txt"] });

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.EndsWith("; a run also holds what --apply needs, named on the line(s) 'held now:' above", dry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing in this request needs correcting", dry.Message, StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.StartsWith($"held now: '{worktree}' on local is held by ", StringComparison.Ordinal));
    }

    /// <summary>
    /// A holder that holds one variant of the tree is named as the one owner of a tree with its host and its variant
    /// spells it - the same words the --apply's own refusal says, in the same order.
    /// </summary>
    [Fact]
    public async Task AVariantHoldingHolder_IsNamedAsTheOneOwnerSpellsATreeWithItsHostAndVariant()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);

        await using var held = await HoldSharedAsync(kit, worktree, RunId.New(), variant: "debug");

        var dry = await kit.FoldAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.Contains(dry.Details!, line => line.StartsWith($"held now: '{worktree}' variant 'debug' on local is held by ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every holder is named, in the lock file's order: two runs each holding one variant of the worktree - which a
    /// fold's --apply, taking the tree whole, refuses both - give two lines, not one.
    /// </summary>
    [Fact]
    public async Task TwoRunsEachHoldingAVariantOfTheWorktree_AreBothNamed_ByTheDryRunThatTakesTheTree()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);

        await using var one = await HoldSharedAsync(kit, worktree, RunId.New(), variant: "debug");
        await using var two = await HoldSharedAsync(kit, worktree, RunId.New(), variant: "release");

        var dry = await kit.FoldAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.Equal(2, dry.Details!.Count(line => line.StartsWith("held now: ", StringComparison.Ordinal)));
        Assert.Contains(dry.Details!, line => line.Contains("variant 'debug'", StringComparison.Ordinal));
        Assert.Contains(dry.Details!, line => line.Contains("variant 'release'", StringComparison.Ordinal));
    }

    /// <summary>The dry runs of delete-agent, refresh-agent and rebase-agent name the run that holds what their applies take, in the same sentence.</summary>
    [Fact]
    public async Task TheDryRunsOfTheOtherThreeVerbsEndInTheSameSentence()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");

        await using (await HoldAsync(kit, worktree))
        {
            var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: false, Token);

            Assert.Equal(HarnessExit.Refused, refreshed.ExitCode);
            Assert.EndsWith(TreeHolders.Blocked, refreshed.Message, StringComparison.Ordinal);
        }

        await using (await HoldAsync(kit, worktree))
        {
            var deleted = await kit.DeleteAsync("ag", apply: false);

            Assert.Equal(HarnessExit.Refused, deleted.ExitCode);
            Assert.EndsWith(TreeHolders.Blocked, deleted.Message, StringComparison.Ordinal);
        }

        OrchestrationKit.Write(kit.Main, "a.txt", "one\ncommitted edit\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a committed change", Token);

        await using (await HoldAsync(kit, worktree))
        {
            var moved = await kit.Harness.AgentService.RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: false, Token);

            Assert.Equal(HarnessExit.Refused, moved.ExitCode);
            Assert.EndsWith(TreeHolders.Blocked, moved.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A fold's --apply takes the main tree beside the worktree, so its dry run asks the lock for both: a run holding the
    /// main tree refuses it, exit 13, saying nothing in the request needs correcting, and is named on a <c>held now:</c>
    /// line.
    /// </summary>
    [Fact]
    public async Task AFoldsDryRun_IsRefusedWhileARunHoldsTheMainTree()
    {
        using var temp = new TempDirectory();
        var (kit, _) = await AFoldToMakeAsync(temp);

        await using var held = await HoldAsync(kit, kit.Main);

        var dry = await kit.FoldAsync("ag", apply: false);

        HeldOffTheMainTree(dry, kit.Main);
    }

    /// <summary>A refresh's --apply takes the main tree beside the worktree: a run holding the main tree refuses its dry run, exit 13, named.</summary>
    [Fact]
    public async Task ARefreshsDryRun_IsRefusedWhileARunHoldsTheMainTree()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");

        await using var held = await HoldAsync(kit, kit.Main);

        var dry = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: false, Token);

        HeldOffTheMainTree(dry, kit.Main);
    }

    /// <summary>A live agent's deletion folds it, so its --apply takes the main tree beside the worktree: a run holding the main tree refuses its dry run, exit 13, named.</summary>
    [Fact]
    public async Task ALiveAgentsDeletionsDryRun_IsRefusedWhileARunHoldsTheMainTree()
    {
        using var temp = new TempDirectory();
        var (kit, _) = await AFoldToMakeAsync(temp);

        await using var held = await HoldAsync(kit, kit.Main);

        var dry = await kit.DeleteAsync("ag", apply: false);

        HeldOffTheMainTree(dry, kit.Main);
    }

    /// <summary>A sound dry run a run holding the main tree refuses: exit 13, its summary ending in the words that waiting is all there is to do, the run on a <c>held now:</c> line.</summary>
    private static void HeldOffTheMainTree(CommandOutcome dry, string main)
    {
        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.EndsWith(TreeHolders.Blocked, dry.Message, StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.StartsWith($"held now: '{main}' on local is held by ", StringComparison.Ordinal));
    }

    /// <summary>
    /// With <c>--json</c>, a dry run of refresh-agent or rebase-agent that a run holds off answers one document, as fold-agent's
    /// and delete-agent's do: request sound - nothing to correct, only to wait - and the run in <c>heldNow</c>.
    /// </summary>
    [Fact]
    public async Task WithJson_HeldDryRunsOfARefreshAndARebase_AnswerSound_NamingTheRun()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\ncommitted edit\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a committed change", Token);
        var run = RunId.New();

        await using var held = await HoldAsync(kit, worktree, run);

        string[][] commands = [["refresh-agent", "o1", "ag", "a.txt", "--json"], ["rebase-agent", "o1", "ag", "--json"]];

        foreach (var command in commands)
        {
            var answered = await CliRunner.RunAsync(command, Token, workingDirectory: kit.Main);

            Assert.Equal(HarnessExit.Refused, answered.ExitCode);
            var document = JsonNode.Parse(answered.StandardOutput.TrimEnd())!;
            Assert.Equal("sound", document["request"]!.GetValue<string>());
            Assert.False(document["applied"]!.GetValue<bool>());
            var holder = document["heldNow"]!.AsArray().Single()!;
            Assert.Equal(worktree, holder["path"]!.GetValue<string>());
            Assert.Equal(run.Value, holder["run"]!.GetValue<string>());
        }
    }

    /// <summary>
    /// A dry run asks the run lock for exactly the trees its --apply takes: a rebase takes the worktree alone, so a run
    /// holding the main tree does not refuse it, and a run holding a copy of the worktree - which no fold, refresh or
    /// rebase writes - is named by none of them.
    /// </summary>
    [Fact]
    public async Task ADryRunAsksOnlyWhatItsApplyTakes()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\ncommitted edit\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a committed change", Token);
        var copy = await LegCopyAsync(kit, Path.Combine(worktree, ".legs", "357e24c"));
        var copyRun = RunId.New();

        await using (await HoldAsync(kit, kit.Main))
        {
            var moved = await kit.Harness.AgentService.RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: false, Token);

            Assert.True(moved.Succeeded, OrchestrationKit.Describe(moved));
        }

        await using var holdingTheCopy = await HoldSharedAsync(kit, copy, copyRun);

        var folded = await kit.FoldAsync("ag", apply: false);
        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: false, Token);
        var movedToo = await kit.Harness.AgentService.RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: false, Token);

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.True(movedToo.Succeeded, OrchestrationKit.Describe(movedToo));
    }

    /// <summary>
    /// A dry run of a deletion that only a leg copy of the worktree keeps is unfinished as its --apply would be - exit
    /// 21, saying the apply would go as far as the removal and could not finish it - and the copy is named in its
    /// family's own words, on its own line, which the ending points at: no <c>held now:</c> line names it. With the
    /// worktree held as well, the dry run is refused, exit 13.
    /// </summary>
    [Fact]
    public async Task ADryRunOfADeletionThatOnlyACopyHolds_IsUnfinishedAsItsApplyWouldBe()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var copy = await LegCopyAsync(kit, Path.Combine(worktree, ".legs", "357e24c"));
        var copyRun = RunId.New();

        await using var holdingTheCopy = await HoldSharedAsync(kit, copy, copyRun);

        var dry = await kit.DeleteAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Incomplete, dry.ExitCode);
        Assert.EndsWith(
            "and nothing in this request needs correcting; --apply would go as far as its removal now and could not finish it, because a run or a sweep holds a "
                + "copy of its worktree, named above with what holds it: run it once that has ended or is stopped, or run it now and run delete-agent again "
                + "afterwards",
            dry.Message,
            StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.StartsWith($"left the leg copy '{copy}' kept in it: a run still running holds it: ", StringComparison.Ordinal));
        Assert.Contains(dry.Details!, line => line.Contains($"run {copyRun.Value}", StringComparison.Ordinal));
        Assert.DoesNotContain(dry.Details!, line => line.StartsWith("held now:", StringComparison.Ordinal));

        await using var held = await HoldAsync(kit, worktree);

        var refused = await kit.DeleteAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.EndsWith(TreeHolders.Blocked, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// With <c>--json</c>, the run holding a leg copy kept in the worktree - all that leaves a deletion's dry run
    /// unfinished - is in <c>heldNow</c>, as every holder that decides the code is: <c>holds</c> <c>copy</c>, named
    /// from its entry in the lock, with where its leg stands, and <c>said</c> the line its family says of the copy, which
    /// is among the lines. The lines, <c>heldNow</c> and the exit code agree.
    /// </summary>
    [Fact]
    public async Task WithJson_ARunHoldingALegCopyKeptInTheWorktree_IsInHeldNow_SaidAsItsLineSaysIt()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var copy = await LegCopyAsync(kit, Path.Combine(worktree, ".legs", "357e24c"));
        var copyRun = RunId.New();

        await using var holdingTheCopy = await HoldSharedAsync(kit, copy, copyRun, variant: "debug", command: "test");

        var answered = await CliRunner.RunAsync(["delete-agent", "o1", "ag", "--json"], Token, workingDirectory: kit.Main);

        Assert.Equal(HarnessExit.Incomplete, answered.ExitCode);
        var document = JsonNode.Parse(answered.StandardOutput.TrimEnd())!;
        Assert.Equal(HarnessExit.Incomplete, document["exitCode"]!.GetValue<int>());
        Assert.Equal("sound", document["request"]!.GetValue<string>());
        var holder = document["heldNow"]!.AsArray().Single()!;
        Assert.Equal("copy", holder["holds"]!.GetValue<string>());
        Assert.Equal(copy, holder["path"]!.GetValue<string>());
        Assert.Equal("local", holder["host"]!.GetValue<string>());
        Assert.Equal("debug", holder["variant"]!.GetValue<string>());
        Assert.Equal(copyRun.Value, holder["run"]!.GetValue<string>());
        Assert.Equal("test", holder["command"]!.GetValue<string>());
        Assert.Equal("no-slot", holder["standing"]!.GetValue<string>());
        var said = holder["said"]!.GetValue<string>();
        Assert.StartsWith($"left the leg copy '{copy}' kept in it: a run still running holds it: ", said, StringComparison.Ordinal);
        Assert.Contains(said, document["lines"]!.AsArray().Select(line => line!.GetValue<string>()));
    }

    /// <summary>
    /// Where a holder's leg stands is said from this machine's record of its heavy legs: one that asked for no slot is
    /// said to be running, one in line is said to wait with how many legs are ahead of it, and one holding a slot is
    /// said to hold it.
    /// </summary>
    [Fact]
    public async Task WhereAHoldersLegStandsIsSaidFromTheRecordOfHeavyLegs()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var waiting = RunId.New();

        await using var held = await HoldAsync(kit, worktree, waiting);

        WriteSlots(
            kit,
            InLine(kit, "/src/first", "run-first", "first"),
            InLine(kit, "/src/second", "run-second", "second"),
            InLine(kit, worktree, waiting.Value));

        var dry = await kit.FoldAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.Contains(
            dry.Details!,
            line => line.StartsWith($"held now: '{worktree}' on local is held by pid {kit.Harness.Identity.CurrentId}, run {waiting.Value}, since ", StringComparison.Ordinal));
        Assert.Contains(dry.Details!, line => line.EndsWith(", whose leg has not started: it waits for a heavy-leg slot, 2 leg(s) ahead of it.", StringComparison.Ordinal));

        WriteSlots(kit, InLine(kit, worktree, waiting.Value));

        var holding = await kit.FoldAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, holding.ExitCode);
        Assert.Contains(holding.Details!, line => line.EndsWith(", whose leg holds a heavy-leg slot: it runs, or starts as soon as the memory and the room allow.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Where a holder's leg stands is joined to the lock's entry by the run and the tree both name: one run on two trees,
    /// holding a slot on the first and waiting on the second, stands as the tree asked about says - not as its other tree
    /// does.
    /// </summary>
    [Fact]
    public async Task OneRunOnTwoTrees_StandsWithTheTreeAskedAbout()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var run = RunId.New();

        await using var held = await HoldAsync(kit, worktree, run);

        WriteSlots(
            kit,
            InLine(kit, "/src/first", run.Value, "first"),
            InLine(kit, "/src/second", "run-other", "other"),
            InLine(kit, worktree, run.Value));

        var dry = await kit.FoldAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.Contains(
            dry.Details!,
            line => line.StartsWith($"held now: '{worktree}' on local is held by ", StringComparison.Ordinal)
                && line.EndsWith(", whose leg has not started: it waits for a heavy-leg slot, 2 leg(s) ahead of it.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Where the worktrees root is a link, a leg naming the worktree through it waits in this machine's line under that
    /// spelling, and the lock keeps its entry so spelled: the fold, which takes the worktree as git names it, meets the
    /// run all the same, the place in line is joined to its entry, so the dry run says the leg waits, naming the worktree
    /// as the run spelled it - and the --apply's refusal names it as the fold asked for it, with the same words after.
    /// </summary>
    [Fact]
    public async Task AHoldersPlaceInLine_IsJoinedToItsEntry_ThoughTheFoldSpellsTheWorktreeAnotherWay()
    {
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp, settings: OrchestrationKit.SettingsWith(root: "wt-link"));
        Directory.Move(Path.Combine(kit.Main, "wt-link"), elsewhere.Combine("wt-real"));
        TestLinks.DirectoryLink(Path.Combine(kit.Main, "wt-link"), elsewhere.Combine("wt-real"));
        await kit.CreateAgentAsync("ag");
        var worktree = WorktreeAddress.Nested(OrchestrationKit.Orchestrator, "ag").PathUnder(Path.Combine(kit.Main, "wt-link"));
        var reached = kit.Harness.FileSystem.ResolveLinks(worktree);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var run = RunId.New();

        await using var held = await HoldAsync(kit, worktree, run);
        WriteSlots(kit, InLine(kit, "/src/first", "run-first", "first"), InLine(kit, worktree, run.Value));

        var dry = await kit.FoldAsync("ag", apply: false);
        var applied = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.Contains(
            dry.Details!,
            line => line.StartsWith($"held now: '{worktree}' on local is held by ", StringComparison.Ordinal)
                && line.EndsWith(", whose leg has not started: it waits for a heavy-leg slot, 1 leg(s) ahead of it.", StringComparison.Ordinal));
        Assert.Equal(HarnessExit.Refused, applied.ExitCode);
        Assert.StartsWith($"'{reached}' on local is held by ", applied.Message, StringComparison.Ordinal);
        Assert.EndsWith(", whose leg has not started: it waits for a heavy-leg slot, 1 leg(s) ahead of it. Nothing was changed; run it again once that is done.", applied.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A dry run of a deletion names the run that holds what its --apply takes whether its own request is refused or not:
    /// refused over a path --merge names that the fold weighs not, and refused because the agent is not whole, both end
    /// in the one clause, with the holder on a line of their own.
    /// </summary>
    [Fact]
    public async Task ADryRunOfADeletionRefusedForItsOwnRequest_NamesTheRunToo_HoweverItIsRefused()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);

        await using (await HoldAsync(kit, worktree))
        {
            var refused = await kit.DeleteAsync("ag", apply: false, new FoldAllowances { Merge = ["src/typo.txt"] });

            Assert.Equal(HarnessExit.Refused, refused.ExitCode);
            Assert.EndsWith(TreeHolders.AlsoHeld, refused.Message, StringComparison.Ordinal);
            Assert.Contains(refused.Details!, line => line.StartsWith($"held now: '{worktree}' on local is held by ", StringComparison.Ordinal));
        }

        File.Delete(kit.Layout.SeedFile("ag"));

        await using (await HoldAsync(kit, worktree))
        {
            var notWhole = await kit.DeleteAsync("ag", apply: false);

            Assert.Equal(HarnessExit.Refused, notWhole.ExitCode);
            Assert.EndsWith(TreeHolders.AlsoHeld, notWhole.Message, StringComparison.Ordinal);
            Assert.Contains(notWhole.Details!, line => line.StartsWith($"held now: '{worktree}' on local is held by ", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A lock file that cannot be read refuses the dry run, exit 13, saying why - its --apply could not take its trees
    /// either - and <c>--json</c> answers with <c>heldNow</c> null and the reason in <c>heldUnread</c>. A record of
    /// heavy legs that cannot be read names the holders without the words that say where their legs stand, and one line
    /// says why; the exit code is what the holder makes it.
    /// </summary>
    [Fact]
    public async Task ALockThatCannotBeReadRefusesTheDryRun_ARecordThatCannotBeReadNamesTheHoldersBarely()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var layout = new HarnessLayout(kit.Main, kit.Main);
        await using var held = await HoldAsync(kit, worktree);
        var slots = kit.Harness.Slots.Location;

        AdmissionKit.Write(slots, InLine(kit, worktree, RunId.New().Value));
        File.WriteAllText(layout.LockFile, "not the lock");

        var dry = await kit.FoldAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.EndsWith("; --apply is refused until the lock file can be read", dry.Message, StringComparison.Ordinal);
        Assert.StartsWith("dry run: folding agent 'ag' of 'o1' writes 1 path(s) into the main tree", dry.Message, StringComparison.Ordinal);

        var answered = await CliRunner.RunAsync(["fold-agent", "o1", "ag", "--json"], Token, workingDirectory: kit.Main);

        Assert.Equal(HarnessExit.Refused, answered.ExitCode);
        var document = JsonNode.Parse(answered.StandardOutput.TrimEnd())!;
        Assert.Null(document["heldNow"]);
        Assert.StartsWith("The run lock '", document["heldUnread"]!.GetValue<string>(), StringComparison.Ordinal);

        File.Delete(layout.LockFile);
        await using var heldAgain = await HoldAsync(kit, worktree);
        File.WriteAllText(slots, "not the record");

        var barely = await kit.FoldAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, barely.ExitCode);
        Assert.Contains(barely.Details!, line => line.StartsWith($"held now: '{worktree}' on local is held by pid {kit.Harness.Identity.CurrentId}", StringComparison.Ordinal));
        Assert.Contains(barely.Details!, line => line.EndsWith(", running 'test'.", StringComparison.Ordinal));
        Assert.DoesNotContain("whose leg", barely.Details![^1], StringComparison.Ordinal);
        Assert.StartsWith("where each run's leg stands cannot be told: ", barely.Details![^1], StringComparison.Ordinal);

        // Where the record could not be read, where the holder's leg stands is not known: never said to be a leg that
        // asked for no slot.
        var unknown = JsonNode.Parse((await kit.Harness.AgentService.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, Token, json: true)).Data[0])!;
        var holder = unknown["heldNow"]!.AsArray().Single()!;

        Assert.Equal(worktree, holder["path"]!.GetValue<string>());
        Assert.Null(holder["standing"]);
        Assert.Null(holder["ahead"]);
    }

    /// <summary>
    /// A lock file that cannot be read refuses a deletion's dry run alike whether a leg copy is kept in the worktree or
    /// not - exit 13, request sound, its summary ending in why its --apply could not take its trees - though the leg
    /// copies' own question, which asks that same lock, cannot be answered either: the --apply is refused at its hold
    /// before it goes anywhere near the removal, so the dry run never says it would go as far as that.
    /// </summary>
    [Fact]
    public async Task ALockThatCannotBeRead_RefusesADeletionsDryRunAlike_WithALegCopyKeptInTheWorktreeOrWithout()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        File.WriteAllText(new HarnessLayout(kit.Main, kit.Main).LockFile, "not the lock");

        var without = JsonNode.Parse((await kit.Harness.AgentService.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token, json: true)).Data[0])!;

        await LegCopyAsync(kit, Path.Combine(worktree, HarnessLayout.LegsDirectoryName, "357e24c"));

        var with = JsonNode.Parse((await kit.Harness.AgentService.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token, json: true)).Data[0])!;

        foreach (var document in new[] { without, with })
        {
            Assert.Equal(HarnessExit.Refused, document["exitCode"]!.GetValue<int>());
            Assert.Equal("sound", document["request"]!.GetValue<string>());
            Assert.EndsWith("; --apply is refused until the lock file can be read", document["summary"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.DoesNotContain("could not finish it", document["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        Assert.Contains(
            with["lines"]!.AsArray().Select(line => line!.GetValue<string>()),
            line => line.StartsWith("the leg copies kept in it could not be removed: ", StringComparison.Ordinal));
    }

    /// <summary>
    /// A lock file that cannot be read refuses a deletion's dry run alike whether a host keeps a copy of the worktree or
    /// not: the copies hosts keep are asked of the lock with the trees the --apply takes, so the one row for a lock that
    /// cannot be read decides - the same request, nobody named, the same reason why and the same lines - never a refusal
    /// that says the lock was read and nobody holds anything.
    /// </summary>
    [Fact]
    public async Task ALockThatCannotBeRead_RefusesADeletionsDryRunAlike_WithAHostsCopyRecordedOrWithout()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var agents = kit.Harness.Agents(
            kit.Harness.FileSystem,
            kit.Harness.AnchorRegistryService,
            hostCopies: new HostCopyRemover(new NoHosts(), new NoHosts(), kit.Harness.RunLock, kit.Harness.FileSystem, kit.Harness.Platform));
        File.WriteAllText(layout.LockFile, "not the lock");

        var without = JsonNode.Parse((await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token, json: true)).Data[0])!;

        new HostCopyRecord(kit.Harness.FileSystem, kit.Harness.Platform.PathComparison)
            .Claim(layout, new HostCopyEntry(WorktreeAddress.Nested("o1", "ag").CopyName, "ssh pi", "/home/pi/repo--wt--o1--ag", worktree));

        var with = JsonNode.Parse((await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token, json: true)).Data[0])!;

        Assert.Equal(HarnessExit.Refused, with["exitCode"]!.GetValue<int>());
        Assert.Equal("sound", with["request"]!.GetValue<string>());
        Assert.Null(with["heldNow"]);
        Assert.StartsWith("The run lock '", with["heldUnread"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.EndsWith("; --apply is refused until the lock file can be read", with["summary"]!.GetValue<string>(), StringComparison.Ordinal);

        foreach (var member in new[] { "exitCode", "summary", "request", "heldNow", "heldUnread", "lines" })
        {
            Assert.Equal(without[member]?.ToJsonString(), with[member]?.ToJsonString());
        }
    }

    /// <summary>
    /// With <c>--json</c>, a holder's leg waiting in line says how many legs are ahead of it - and one holding a slot says
    /// none, as no line is ahead of it.
    /// </summary>
    [Fact]
    public async Task WithJson_AHoldersLegInLineSaysHowManyAreAhead_AndOneHoldingASlotNone()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var run = RunId.New();

        await using var held = await HoldAsync(kit, worktree, run);

        WriteSlots(kit, InLine(kit, "/src/first", "run-first", "first"), InLine(kit, "/src/second", "run-second", "second"), InLine(kit, worktree, run.Value));

        var waiting = JsonNode.Parse((await kit.Harness.AgentService.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, Token, json: true)).Data[0])!["heldNow"]!.AsArray().Single()!;

        Assert.Equal("waiting", waiting["standing"]!.GetValue<string>());
        Assert.Equal(2, waiting["ahead"]!.GetValue<int>());

        WriteSlots(kit, InLine(kit, worktree, run.Value));

        var holding = JsonNode.Parse((await kit.Harness.AgentService.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, Token, json: true)).Data[0])!["heldNow"]!.AsArray().Single()!;

        Assert.Equal("holds-slot", holding["standing"]!.GetValue<string>());
        Assert.Null(holding["ahead"]);
    }

    /// <summary>
    /// A leg on an ssh host asks that host for its slot, as that host's own record says, whatever this machine's record
    /// holds: where this machine's record cannot be read, a run holding a host's copy of the worktree is still said to ask
    /// that host - on its <c>held now:</c> line and as <c>on-host</c> in <c>heldNow</c> - and only a leg of this machine
    /// is said without where it stands. This machine's record is read only for a leg of this machine, so neither that
    /// holder nor nobody at all has a line say it cannot be read.
    /// </summary>
    [Fact]
    public async Task AnSshHoldersLegAsksThatHost_WhereThisMachinesRecordOfHeavyLegsCannotBeRead()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var copy = "/home/pi/repo--wt--o1--ag";
        var agents = kit.Harness.Agents(
            kit.Harness.FileSystem,
            kit.Harness.AnchorRegistryService,
            hostCopies: new HostCopyRemover(new NoHosts(), new NoHosts(), kit.Harness.RunLock, kit.Harness.FileSystem, kit.Harness.Platform));
        new HostCopyRecord(kit.Harness.FileSystem, kit.Harness.Platform.PathComparison)
            .Claim(layout, new HostCopyEntry(WorktreeAddress.Nested("o1", "ag").CopyName, "ssh pi", copy, worktree));

        Directory.CreateDirectory(Path.GetDirectoryName(kit.Harness.Slots.Location)!);
        File.WriteAllText(kit.Harness.Slots.Location, "not the record");

        var free = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token);

        Assert.True(free.Succeeded, OrchestrationKit.Describe(free));
        Assert.DoesNotContain(free.Details ?? [], line => line.StartsWith("where each run's leg stands cannot be told: ", StringComparison.Ordinal));

        var attempt = await kit.Harness.RunLock.TryAcquireAsync(
            layout,
            new LockRequest { Host = "ssh pi", Tree = copy, Scope = LockScope.TreeExclusive, RunId = RunId.New(), Command = "test" },
            Token);

        await using var held = attempt.Handle ?? throw new InvalidOperationException($"the test could not hold '{copy}': {attempt.HeldBy}");

        var dry = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token);

        Assert.Contains(
            dry.Details!,
            line => line.StartsWith($"held now: '{copy}' on ssh pi is held by ", StringComparison.Ordinal)
                && line.EndsWith(", whose leg asks that host for its slot: whether it runs or waits is recorded there.", StringComparison.Ordinal));
        Assert.DoesNotContain(dry.Details!, line => line.StartsWith("where each run's leg stands cannot be told: ", StringComparison.Ordinal));

        var document = JsonNode.Parse((await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token, json: true)).Data[0])!;

        Assert.Equal("on-host", document["heldNow"]!.AsArray().Single()!["standing"]!.GetValue<string>());
    }

    /// <summary>
    /// A leg in a WSL distribution asks this machine for its slot - the command that dispatched it asks for it here, where
    /// it runs - so a run holding a WSL distribution's copy of the worktree stands as this machine's line holds it: holding
    /// a slot, on its <c>held now:</c> line and as <c>holds-slot</c> in <c>heldNow</c>, never said to ask that host.
    /// </summary>
    [Fact]
    public async Task AWslHoldersLegStandsAsThisMachinesLineHoldsIt()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var wsl = HostId.Wsl("Ubuntu").ToString();
        var copy = "/home/u/repo--wt--o1--ag";
        var run = RunId.New();
        var agents = kit.Harness.Agents(
            kit.Harness.FileSystem,
            kit.Harness.AnchorRegistryService,
            hostCopies: new HostCopyRemover(new NoHosts(), new NoHosts(), kit.Harness.RunLock, kit.Harness.FileSystem, kit.Harness.Platform));
        new HostCopyRecord(kit.Harness.FileSystem, kit.Harness.Platform.PathComparison)
            .Claim(layout, new HostCopyEntry(WorktreeAddress.Nested("o1", "ag").CopyName, wsl, copy, worktree));
        var attempt = await kit.Harness.RunLock.TryAcquireAsync(
            layout,
            new LockRequest { Host = wsl, Tree = copy, Scope = LockScope.TreeExclusive, RunId = run, Command = "test" },
            Token);

        await using var held = attempt.Handle ?? throw new InvalidOperationException($"the test could not hold '{copy}': {attempt.HeldBy}");

        WriteSlots(kit, InLine(kit, copy, run.Value, host: wsl));

        var dry = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token);

        Assert.Contains(
            dry.Details!,
            line => line.StartsWith($"held now: '{copy}' on {wsl} is held by ", StringComparison.Ordinal)
                && line.EndsWith(", whose leg holds a heavy-leg slot: it runs, or starts as soon as the memory and the room allow.", StringComparison.Ordinal));

        var document = JsonNode.Parse((await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token, json: true)).Data[0])!;

        Assert.Equal("holds-slot", document["heldNow"]!.AsArray().Single()!["standing"]!.GetValue<string>());
    }

    /// <summary>
    /// The lock is read once for everything a deletion's dry run asks of it - the trees its --apply takes, the copies hosts
    /// keep, and each copy kept in the worktree that a run holds - so every holder is named from that one reading, never a
    /// copy's holder from a later one the lock may have changed by.
    /// </summary>
    [Fact]
    public async Task TheLockIsReadOnce_ForTheTreesTheHostsCopiesAndEachCopyKeptInTheWorktree()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var reads = new LockReadsCounted(harness.FileSystem, layout.LockFile);
        var runLock = new RunLock(reads, harness.Output, harness.Identity);
        var holders = new TreeHolders(runLock, harness.Slots, harness.Identity);
        var worktree = temp.Combine("wt");
        var copies = new[] { Path.Combine(worktree, ".legs", "357e24c"), Path.Combine(worktree, ".legs", "9a0b1c2") };
        var handles = new List<RunLockHandle>();

        foreach (var copy in copies)
        {
            var attempt = await runLock.TryAcquireAsync(layout, Core.Legs.LegCopies.Whole(copy, "test"), Token);
            handles.Add(attempt.Handle ?? throw new InvalidOperationException($"the test could not hold '{copy}': {attempt.HeldBy}"));
        }

        try
        {
            var kept = copies.Select(copy => KeptCopyLine.Held(copy, $"left the leg copy '{copy}' kept in it", Core.Legs.LegCopies.Whole(copy, WorktreeService.DeleteCommand))).ToList();

            reads.Count = 0;

            var held = holders.Read(
                layout,
                [new LockRequest { Host = HostId.Local.ToString(), Tree = worktree, Scope = LockScope.TreeExclusive, RunId = RunId.New(), Command = AgentService.DeleteCommand }],
                [HostCopyRemover.RemovalLock(HostId.Ssh("pi"), "/home/pi/repo--wt--wt", RunId.New())],
                kept);

            Assert.Equal(1, reads.Count);
            Assert.Null(held.Unread);
            Assert.Equal(copies, held.Holders.Select(holder => holder.Path));
            Assert.All(held.Holders, holder => Assert.True(holder.HoldsKeptCopy));
        }
        finally
        {
            foreach (var handle in handles)
            {
                await handle.DisposeAsync();
            }
        }
    }

    /// <summary>The real file system, counting how often one file - the lock - is read.</summary>
    private sealed class LockReadsCounted(Core.FileSystem.IFileSystem inner, string counted) : PassThroughFileSystem(inner)
    {
        /// <summary>How often the file was read since this was last set.</summary>
        public int Count { get; set; }

        public override string ReadAllText(string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(counted), StringComparison.OrdinalIgnoreCase))
            {
                Count++;
            }

            return base.ReadAllText(path);
        }
    }

    /// <summary>
    /// A leg copy is asked of the lock as a run takes it, whole on this machine, under the command that asks - clean's, or a
    /// deletion's - never under another command's name.
    /// </summary>
    [Fact]
    public void ALegCopyIsAskedOfTheLockWhole_UnderTheCommandThatAsks()
    {
        var asked = Core.Legs.LegCopies.Whole("/src/repo/.legs/357e24c", WorktreeService.DeleteCommand);

        Assert.Equal((HostId.Local.ToString(), "/src/repo/.legs/357e24c", LockScope.TreeExclusive, WorktreeService.DeleteCommand), (asked.Host, asked.Tree, asked.Scope, asked.Command));
        Assert.Equal(Core.Runs.CleanService.CommandName, Core.Legs.LegCopies.Whole("/src/repo/.legs/357e24c", Core.Runs.CleanService.CommandName).Command);
    }

    /// <summary>
    /// README and the architecture say what a deletion's dry run answers as the code decides it: 21 where its --apply
    /// would go as far as the removal and leave it unfinished, over a copy a run or a sweep holds or copies kept in the
    /// worktree it cannot remove as they stand, only below the rows that refuse that --apply at its hold; what each line
    /// the families of copies say is to it, a leg copy's holder asked of the lock as its removal asks it; and the members
    /// of <c>heldNow</c> for each holder, a claim's as that claim records them.
    /// </summary>
    [Fact]
    public void TheDocumentsSayWhatADeletionsDryRunAnswers_AsTheCodeDecidesIt()
    {
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "and `21` where a deletion's `--apply` would go as far as the removal and leave it unfinished, over a copy a run or a "
                + "sweep holds or copies kept in the worktree it cannot remove as they stand.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "and whose `heldNow` holds each holder, those copies' among them.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            $"`{nameof(DryRuns)}.{nameof(DryRuns.Answer)}` is the one place that decides it",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "a sound dry run of a deletion whose copies kept in the worktree the removal cannot take as they stand - a copy that "
                + "cannot be told or removed, or copies that cannot be looked for - 21, its request refused, since every `--apply` "
                + "leaves the removal there until that is put right, which no waiting does "
                + $"(`{nameof(KeptCopyLine)}.{nameof(KeptCopyLine.Stops)}`, the summary ending in `{nameof(DryRuns)}.{nameof(DryRuns.Unremovable)}`), "
                + "over a copy a run or a sweep holds as well, while a directory there that no sync made goes with the worktree and "
                + "is only said; one that only a run or a sweep holding a copy of the worktree keeps, 21 "
                + $"(`{nameof(TreeHolders)}.{nameof(TreeHolders.RemovalHeld)}`), since its `--apply` would go as far as the removal and "
                + "leave it unfinished; otherwise 0. Neither of those two is reached where a lock file cannot be read or a run holds "
                + "a tree the `--apply` takes, which refuse that `--apply` at its hold, so a dry run never says the `--apply` would go "
                + "as far as the removal where it would not.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Each line the two families say is one of three (`{nameof(KeptCopyLine)}`): a copy a run or a sweep holds, whose "
                + "holder that line names - a leg copy's asked of the lock as its removal asks it "
                + $"(`{nameof(Core.Legs.LegCopies)}.{nameof(Core.Legs.LegCopies.Whole)}`), in that same one reading, so every holder is "
                + "named from it, a worker's named by its sweep's claim - and no `held now:` line names again; what the removal "
                + "cannot take as it stands; or what is only said.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "each holding a copy kept in the worktree, its `said` the line its family says of it - its `processId`, `run` and "
                + "`since` as the lock's entry records them or, where a claim of the copy's family rather than the lock names its "
                + $"holder, as that claim records them (`{nameof(CopyHolder)}`), and what only a lock entry says, `command`, "
                + "`variant` and `standing`, null for such a holder; `standing` is null too for a leg of this machine where this "
                + "machine's record of heavy legs cannot be read - and `heldUnread`, why the lock file could not be read.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "a run that asked for no slot is said by the lock's own words alone, `running '<command>'`, and so is every run of a "
                + "leg of this machine - a WSL distribution's among them, whose slot this machine's record holds - where that "
                + "record cannot be read, which one line then says, while a leg on an ssh host asks that host for its slot "
                + "whatever this machine's record holds, and that record is read only where a holder's leg is one of this "
                + "machine's, so no line says it cannot be read where none is).",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "names a copy a run holds - a host's through the lock, asked in the one reading of it that asks for the trees "
                + $"(`{nameof(IHostCopyRemover)}.{nameof(IHostCopyRemover.RemovalLocks)}`, the same `{nameof(HostCopyRemover.RemovalLock)}` "
                + $"the removal takes, asked by `{nameof(TreeHolders)}.{nameof(TreeHolders.Read)}`, so a lock that cannot be read is said "
                + "by the one row for it, a host's copy recorded or not)",
            architecture,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The help, README and the architecture say how the lock keys a tree on this machine - every link followed, one key
    /// however a command spells it, a build before this one included, each entry kept as spelled - what a removal of a
    /// host's copy takes with it, a sweep's key there among it, and that no sweep starts there while it goes, what an
    /// --apply refused at its hold answers with <c>--json</c>, and that a usage error and a record of host copies that
    /// cannot be read answer a dry run as they answer its --apply.
    /// </summary>
    [Fact]
    public async Task TheDocumentsSayHowTheLockKeysATree_WhatARemovalTakes_AndWhatAnApplyHeldOffAnswers()
    {
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");
        var worktrees = Prose.Words((await CliRunner.RunAsync(["help", "worktrees"], Token)).StandardOutput);

        Assert.Contains(
            "The run lock knows a tree on this machine by where it leads, every link followed, so worktrees kept on another disk "
                + "through a worktrees root that is a link, and copies kept through a `.legs` or `.mutations` that is one, are held, and "
                + "asked about, as one tree however a command spells them - a build before this one's too, since the lock keeps each "
                + "tree as its run spelled it.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "`10` where its arguments are a usage error, which that `--apply` answers before it takes anything,",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("An `--apply` refused at its hold answers the same document, `sound`, with the run it met in `heldNow`.", readme, StringComparison.Ordinal);
        Assert.Contains(
            $"A tree on this machine is **one key however it is reached** (`{nameof(RunLock)}.{nameof(RunLock.Key)}`): its full path with every "
                + "link and junction along it followed, as git spells a tree, told whenever the lock is read - by a run, a dry run, a clean "
                + "or a deletion alike - for each entry and each request, once a reading. An entry keeps its tree as its run spelled it, so a "
                + "build before this one, which compares trees as spelled, still meets what it spelled alike",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"An agent's trees are taken as git names them (`{nameof(AgentService)}.Holds`), as builds before this one took them.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"A **removal** takes the tree with everything kept inside it: a tree taken whole under `{WorktreeService.DeleteCommand}` "
                + $"(`{nameof(RunLock)}.{nameof(RunLock.RemovalCommand)}`), as every removal of a host's copy is recorded, by this build and "
                + "those before it, rather than by a field of its own, which an older build would refuse the lock file over. Asked for, an "
                + "entry on that host for anything inside the tree - a sweep's key in a host's copy - is in its way, as one for the tree is; "
                + "standing, its entry is in the way of whatever is asked inside the tree, so no sweep starts in a copy while it is removed.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"taken with everything kept inside the copy (`{nameof(RunLock)}.{nameof(RunLock.RemovalCommand)}`, on "
                + $"`{nameof(HostCopyRemover)}.{nameof(HostCopyRemover.RemovalLock)}`), so a sweep this machine runs there - its key in the "
                + "copy's `.mutations` - keeps the copy as a run in it does, and the dry run of a deletion names it as one, while no sweep "
                + "takes a key there once the removal holds the copy;",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "An `--apply` refused at its hold answers the same document, `applied` true, its `request` `sound` and the run it met in "
                + $"`heldNow`, said as a dry run at that moment says it (`{nameof(TreeHolders)}.{nameof(TreeHolders.Refused)}`",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "or, for a deletion, a record of the copies hosts keep that cannot be read, which its `--apply` reads before it writes "
                + "anything (`AgentService.HostCopiesOf`, the lines weighed kept) - 13, save a usage error, 10, which its `--apply` answers "
                + "before it takes anything;",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "save where what the command weighs could not be read, or the request is a usage error, either of which keeps its own "
                + "code from above",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "A copy is removed under the lock a leg this machine runs there takes, taken with everything kept inside the copy, so a "
                + "sweep this machine runs there keeps it as a run does, none starts there while it goes,",
            worktrees,
            StringComparison.Ordinal);
        Assert.Contains($"{HarnessExit.Refused} where a run or a sweep holds one or it was refused,", worktrees, StringComparison.Ordinal);
    }

    /// <summary>
    /// With <c>--json</c> the outcome is one document: a sound dry run that is held answers exit 13 with
    /// <c>request</c> <c>sound</c> and the run in <c>heldNow</c>, its <c>holds</c> <c>tree</c>; a refused request answers
    /// <c>refused</c>, held or not; an <c>--apply</c> that wrote answers <c>applied</c> true and <c>request</c>
    /// <c>sound</c>; and the lines are the lines printed without it, in order.
    /// </summary>
    [Fact]
    public async Task WithJson_TheOutcomeIsOneDocumentHoldingTheRequestAndTheHolders()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);

        await using (await HoldAsync(kit, worktree))
        {
            var dry = await CliRunner.RunAsync(["fold-agent", "o1", "ag", "--json"], Token, workingDirectory: kit.Main);

            Assert.Equal(HarnessExit.Refused, dry.ExitCode);
            var document = JsonNode.Parse(dry.StandardOutput.TrimEnd())!;
            Assert.Equal(HarnessExit.Refused, document["exitCode"]!.GetValue<int>());
            Assert.Equal("sound", document["request"]!.GetValue<string>());
            Assert.False(document["applied"]!.GetValue<bool>());
            var holder = document["heldNow"]!.AsArray().Single();
            Assert.Equal("tree", holder!["holds"]!.GetValue<string>());
            Assert.Equal(worktree, holder["path"]!.GetValue<string>());
            Assert.Equal("local", holder["host"]!.GetValue<string>());
            Assert.Equal("test", holder["command"]!.GetValue<string>());
            Assert.Equal("no-slot", holder["standing"]!.GetValue<string>());
            Assert.StartsWith($"'{worktree}' on local is held by pid ", holder["said"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Contains(
                document["lines"]!.AsArray().Select(line => line!.GetValue<string>()),
                line => line.StartsWith("held now: ", StringComparison.Ordinal));
        }

        await using (await HoldAsync(kit, worktree))
        {
            var refused = await CliRunner.RunAsync(["fold-agent", "o1", "ag", "--merge", "src/typo.txt", "--json"], Token, workingDirectory: kit.Main);

            Assert.Equal(HarnessExit.Refused, refused.ExitCode);
            Assert.Equal("refused", JsonNode.Parse(refused.StandardOutput.TrimEnd())!["request"]!.GetValue<string>());
        }

        var applied = await CliRunner.RunAsync(["fold-agent", "o1", "ag", "--apply", "--json"], Token, workingDirectory: kit.Main);

        Assert.Equal(0, applied.ExitCode);
        var written = JsonNode.Parse(applied.StandardOutput.TrimEnd())!;
        Assert.True(written["applied"]!.GetValue<bool>());
        Assert.Equal("sound", written["request"]!.GetValue<string>());
        Assert.Empty(written["heldNow"]!.AsArray());
    }

    /// <summary>
    /// The refusal an <c>--apply</c> gives where a run holds what it needs ends with the same words the dry run's
    /// <c>held now:</c> line says of where that run's leg stands - and seed-agent's refusal, which takes the trees at
    /// once, does too.
    /// </summary>
    [Fact]
    public async Task TheRefusalAnApplyGains_TheSameWordsTheDryRunsLineSaysOfTheLeg()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var run = RunId.New();

        await using var held = await HoldAsync(kit, worktree, run);
        WriteSlots(kit, InLine(kit, "/src/first", "run-first", "first"), InLine(kit, worktree, run.Value));

        var folded = await kit.FoldAsync("ag", apply: true);
        var seeded = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: true, Token);

        foreach (var outcome in new[] { folded, seeded })
        {
            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.Contains(
                $"is held by pid {kit.Harness.Identity.CurrentId}, run {run.Value}, since ",
                outcome.Message,
                StringComparison.Ordinal);
            Assert.EndsWith(
                ", running 'test', whose leg has not started: it waits for a heavy-leg slot, 1 leg(s) ahead of it. Nothing was changed; run it again once that is done.",
                outcome.Message,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// fold-agent's --apply refused at its hold answers <c>--json</c> as its dry run at that moment does: request sound,
    /// since nothing in it needs correcting, and the run it met in <c>heldNow</c> - the holder a script waits for - said in
    /// the words a <c>held now:</c> line says.
    /// </summary>
    [Fact]
    public async Task AFoldsApplyRefusedAtItsHold_AnswersSound_NamingTheHolderItMet()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var run = RunId.New();

        await using var held = await HoldAsync(kit, worktree, run);

        var answered = await kit.Harness.AgentService.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, Token, json: true);

        AnsweredAsHeld(answered, worktree, run);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>refresh-agent's --apply refused at its hold answers <c>--json</c> with request sound and the run it met in <c>heldNow</c>.</summary>
    [Fact]
    public async Task ARefreshsApplyRefusedAtItsHold_AnswersSound_NamingTheHolderItMet()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var run = RunId.New();

        await using var held = await HoldAsync(kit, worktree, run);

        var answered = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: true, Token, json: true);

        AnsweredAsHeld(answered, worktree, run);
        Assert.Equal("one\n", OrchestrationKit.Read(worktree, "a.txt"));
    }

    /// <summary>rebase-agent's --apply refused at its hold answers <c>--json</c> with request sound and the run it met in <c>heldNow</c>.</summary>
    [Fact]
    public async Task ARebasesApplyRefusedAtItsHold_AnswersSound_NamingTheHolderItMet()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base;
        OrchestrationKit.Write(kit.Main, "a.txt", "one\ncommitted edit\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a committed change", Token);
        var run = RunId.New();

        await using var held = await HoldAsync(kit, worktree, run);

        var answered = await kit.Harness.AgentService.RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token, json: true);

        AnsweredAsHeld(answered, worktree, run);
        Assert.Equal(from, kit.Record("ag").Base);
    }

    /// <summary>delete-agent's --apply refused at its hold answers <c>--json</c> with request sound and the run it met in <c>heldNow</c>.</summary>
    [Fact]
    public async Task ADeletionsApplyRefusedAtItsHold_AnswersSound_NamingTheHolderItMet()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var run = RunId.New();

        await using var held = await HoldAsync(kit, worktree, run);

        var answered = await kit.Harness.AgentService.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token, json: true);

        AnsweredAsHeld(answered, worktree, run);
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
    }

    /// <summary>
    /// What an --apply refused at its hold answers with <c>--json</c>: exit 13, its cause the lock that run holds, and one
    /// document - applied, request sound, its summary the refusal's, and the one run it met in <c>heldNow</c>, holding
    /// <paramref name="tree"/>, said as a <c>held now:</c> line says it.
    /// </summary>
    private static void AnsweredAsHeld(CommandOutcome answered, string tree, RunId run)
    {
        Assert.Equal(HarnessExit.Refused, answered.ExitCode);
        Assert.EndsWith("Nothing was changed; run it again once that is done.", answered.Message, StringComparison.Ordinal);

        // The one refusal a caller waits out, said beside the code as the document is: a run in the way, nothing to correct.
        Assert.Equal(OutcomeCause.LockHeld, answered.Cause);
        var document = JsonNode.Parse(answered.Data.Single())!;
        Assert.Equal(HarnessExit.Refused, document["exitCode"]!.GetValue<int>());
        Assert.True(document["applied"]!.GetValue<bool>());
        Assert.Equal("sound", document["request"]!.GetValue<string>());
        Assert.Equal(answered.Message, document["summary"]!.GetValue<string>());
        var holder = document["heldNow"]!.AsArray().Single()!;
        Assert.Equal("tree", holder["holds"]!.GetValue<string>());
        Assert.Equal(tree, holder["path"]!.GetValue<string>());
        Assert.Equal("local", holder["host"]!.GetValue<string>());
        Assert.Equal(run.Value, holder["run"]!.GetValue<string>());
        Assert.Equal("test", holder["command"]!.GetValue<string>());
        Assert.Equal("no-slot", holder["standing"]!.GetValue<string>());
        Assert.StartsWith($"'{tree}' on local is held by pid ", holder["said"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>A dry run writes neither the lock file nor the record of heavy legs, and reclaims nothing: both are as they were.</summary>
    [Fact]
    public async Task ADryRunWritesNeitherTheLockFileNorTheRecordOfHeavyLegs()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var layout = new HarnessLayout(kit.Main, kit.Main);
        await using var held = await HoldAsync(kit, worktree);

        var before = File.ReadAllText(layout.LockFile);
        WriteSlots(kit, InLine(kit, "/src/first", "run-first", "first"));

        await kit.FoldAsync("ag", apply: false);

        Assert.Equal(before, File.ReadAllText(layout.LockFile));
        Assert.Single(AdmissionKit.Read(kit.Harness.Slots.Location));
    }

    /// <summary>
    /// A gone agent's dry run asks the lock for the worktree as its --apply spells it, links resolved: where the worktrees
    /// root is a link, a run holding the worktree's reached path is named and the dry run is refused as its --apply is,
    /// never exiting as though the tree were free.
    /// </summary>
    [Fact]
    public async Task AGoneAgentsDryRun_ResolvesTheWorktreesLinks_AsItsApplyDoes()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp, settings: OrchestrationKit.SettingsWith(root: "wt-link"));
        var reached = temp.Combine("wt-real");
        Directory.Move(Path.Combine(kit.Main, "wt-link"), reached);
        TestLinks.DirectoryLink(Path.Combine(kit.Main, "wt-link"), reached);
        await kit.CreateAgentAsync("ag");

        // Spelled through the link, as the agent's record spells it - which is what makes resolving it matter.
        var worktree = WorktreeAddress.Nested(OrchestrationKit.Orchestrator, "ag").PathUnder(Path.Combine(kit.Main, "wt-link"));
        Directory.Delete(worktree, true);
        var resolved = kit.Harness.FileSystem.ResolveLinks(worktree);

        await using var held = await HoldAsync(kit, resolved);

        var dry = await kit.DeleteAsync("ag", apply: false, discard: true);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.EndsWith(TreeHolders.Blocked, dry.Message, StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.StartsWith($"held now: '{resolved}' on local is held by ", StringComparison.Ordinal));
    }
}
