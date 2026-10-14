using System.Text.Json.Nodes;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

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

    /// <summary>An entry of the heavy legs' record for the run holding <paramref name="tree"/>, this process standing for it.</summary>
    private static SlotEntry InLine(OrchestrationKit kit, string tree, string run, string leg = "the-holder", int slots = 1)
        => new(kit.Harness.Identity.CurrentMachine, kit.Harness.Identity.CurrentId, run, DateTimeOffset.UtcNow, "test", leg, "local", tree, slots, kit.Harness.Identity.Current);

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
    /// 21, saying the apply would fold and close it and could not finish the removal - and the copy is named in its
    /// family's own words. With the worktree held as well, the dry run is refused, exit 13.
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
            "and nothing in this request needs correcting; --apply would fold and close it now and could not finish its removal, because a run holds a copy of its "
                + "worktree, named on the line(s) 'held now:' above: run it once that run has ended or is stopped, or run it now and run delete-agent again afterwards",
            dry.Message,
            StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.StartsWith($"left the leg copy '{copy}' kept in it: a run still running holds it: ", StringComparison.Ordinal));
        Assert.Contains(dry.Details!, line => line.Contains($"run {copyRun.Value}", StringComparison.Ordinal));

        await using var held = await HoldAsync(kit, worktree);

        var refused = await kit.DeleteAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.EndsWith(TreeHolders.Blocked, refused.Message, StringComparison.Ordinal);
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
}
