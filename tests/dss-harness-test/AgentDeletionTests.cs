using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Build;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>
/// delete-agent: an agent's fold and rows put in, its evidence and transcripts kept and read back, its closing recorded,
/// and only then its worktree removed and the removal proved; run again, a closed agent is finished, never folded.
/// </summary>
public sealed class AgentDeletionTests
{
    private static readonly Dictionary<string, string> Row = new()
    {
        ["status"] = "open",
        ["priority"] = "P2",
        ["trigger"] = "something the agent found",
        ["closing"] = "the fix",
        ["cross-refs"] = "b.txt",
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>What a sound dry run of a deletion ends with where what is kept in the worktree cannot be removed as it stands.</summary>
    private const string Unremovable = "; --apply would go as far as its removal now and could not finish it, because the copies kept in its worktree cannot be "
        + "removed as they stand, said above: put that right, then run it - or run it now and run delete-agent again once that is done";

    /// <summary>
    /// Deleting an agent folds it and applies its rows, keeps its evidence and its Claude transcripts in its directory,
    /// then removes its worktree; its record says it was deleted, and its directory stays.
    /// </summary>
    [Fact]
    public async Task DeletingAnAgent_FoldsItAndAppliesItsRows_KeepsItsEvidenceAndTranscripts_ThenRemovesItsWorktree()
    {
        using var temp = new TempDirectory();
        using var claude = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        kit.Harness.ClaudeConfigDirectory = claude.Path;
        claude.WriteFile(Path.Combine("projects", "some-project", "sess1.jsonl"), "{\"type\":\"user\"}\n");
        claude.WriteFile(Path.Combine("projects", "some-project", "parent", "subagents", "agent-sess1.jsonl"), "{\"type\":\"assistant\"}\n");
        var worktree = await kit.CreateAgentAsync("ag", session: "sess1");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        kit.FileRow("ag", "D-TEST-AGENT-ROW", Row);

        var dry = await kit.DeleteAsync("ag", apply: false, OrchestrationKit.Making("D-TEST-AGENT-ROW"));

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.StartsWith("dry run: deleting agent 'ag' of 'o1' folds 1 path(s), removes 0 and applies 1 row(s), keeps its transcripts and 1 evidence file(s)", dry.Message);
        Assert.True(Directory.Exists(worktree));
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.DoesNotContain("D-TEST-AGENT-ROW", OrchestrationKit.Read(kit.Main, Path.Combine(".plans", "_deferred-anchor-registry.md")));
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
        Assert.False(Directory.Exists(kit.Layout.EvidenceDirectory("ag")));

        var deleted = await kit.DeleteAsync("ag", apply: true, OrchestrationKit.Making("D-TEST-AGENT-ROW"));
        var record = kit.Record("ag");

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Equal(AgentStates.Deleted, record.State);
        Assert.False(record.Abandoned);
        Assert.False(Directory.Exists(worktree));
        Assert.DoesNotContain(await kit.Harness.WorktreeService.ListAsync(kit.Main, Token), listed => listed.Name == "o1/ag");
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Contains("D-TEST-AGENT-ROW", OrchestrationKit.Read(kit.Main, Path.Combine(".plans", "_deferred-anchor-registry.md")));

        var kept = Assert.Single(Directory.GetDirectories(kit.Layout.EvidenceDirectory("ag")));
        Assert.Equal("measured\n", OrchestrationKit.Read(kept, Path.Combine("evidence", "run.log")));
        Assert.Equal("{\"type\":\"user\"}\n", OrchestrationKit.Read(kit.Layout.TranscriptsDirectory("ag"), Path.Combine("some-project", "sess1.jsonl")));
        Assert.True(File.Exists(Path.Combine(kit.Layout.TranscriptsDirectory("ag"), "some-project", "parent", "subagents", "agent-sess1.jsonl")));
        Assert.Contains(deleted.Details!, line => line.StartsWith("kept 2 transcript file(s) of session sess1", StringComparison.Ordinal));
        Assert.Contains(kit.Harness.OrchestrationLog.Read(kit.Layout.LogFile("ag")), entry => entry.Command == AgentService.DeleteCommand && entry.Outcome == nameof(HarnessExit.Success));
    }

    /// <summary>--discard-uncommitted abandons an agent: nothing of it is folded, and its evidence is still kept.</summary>
    [Fact]
    public async Task AbandoningAnAgent_FoldsNothing_AndStillKeepsItsEvidence()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        kit.FileRow("ag", "D-TEST-AGENT-ROW", Row);

        var deleted = await kit.DeleteAsync("ag", apply: true, discard: true);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.True(kit.Record("ag").Abandoned);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.DoesNotContain("D-TEST-AGENT-ROW", OrchestrationKit.Read(kit.Main, Path.Combine(".plans", "_deferred-anchor-registry.md")));
        Assert.Single(Directory.GetDirectories(kit.Layout.EvidenceDirectory("ag")));
        Assert.False(Directory.Exists(worktree));
    }

    /// <summary>
    /// A file written into an evidence root after the evidence was kept stops the removal, which never waives its evidence
    /// check: the agent is left closed, and deleting it again keeps the late file too and finishes, folding nothing.
    /// </summary>
    [Fact]
    public async Task ALateEvidenceFile_StopsTheRemoval_AndDeletingAgainFinishesIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        var late = new LateEvidenceFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "evidence", "late.log"));
        var agents = kit.Harness.Agents(late, kit.Harness.AnchorRegistryService);

        var stopped = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' is closed and not deleted yet: ", stopped.Message);
        Assert.Contains("Its fold and rows are in the main tree, and it is closed, so nothing folds it again.", stopped.Message);
        Assert.Equal(AgentStates.Closed, kit.Record("ag").State);
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));

        OrchestrationKit.Write(kit.Main, "b.txt", "two\na sibling's fold since\n");
        var allowed = await kit.DeleteAsync("ag", apply: true, new FoldAllowances { Settled = ["b.txt"], AcceptLost = ["D-TEST-AGENT-ROW:closing"] });

        Assert.Equal(HarnessExit.UsageError, allowed.ExitCode);
        Assert.StartsWith("--settled, --accept-lost let a fold through what it otherwise refuses, and agent 'ag' of 'o1', closed at ", allowed.Message);
        Assert.EndsWith(", is never folded again. Nothing was read, written or removed.", allowed.Message);

        var finished = await kit.DeleteAsync("ag", apply: true);

        Assert.True(finished.Succeeded, OrchestrationKit.Describe(finished));
        Assert.Equal(AgentStates.Deleted, kit.Record("ag").State);
        Assert.Equal("two\na sibling's fold since\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Contains(Directory.GetDirectories(kit.Layout.EvidenceDirectory("ag")), directory => File.Exists(Path.Combine(directory, "evidence", "late.log")));
        Assert.False(Directory.Exists(worktree));
    }

    /// <summary>
    /// A closed agent holding work done since it was closed is left for a person: nothing folds it again, and nothing
    /// discards it unseen.
    /// </summary>
    [Fact]
    public async Task AClosedAgent_HoldingWorkDoneSince_IsLeftForAPerson()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        var agents = kit.Harness.Agents(new LateEvidenceFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "evidence", "late.log")), kit.Harness.AnchorRegistryService);
        Assert.Equal(HarnessExit.Incomplete, (await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token)).ExitCode);
        OrchestrationKit.Write(worktree, "b.txt", "two\nwork after the closing\n");

        var left = await kit.DeleteAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Incomplete, left.ExitCode);
        Assert.Contains("its worktree holds 1 file(s) its closing did not record", left.Message);
        Assert.Equal(["  b.txt", $"record {kit.Layout.AgentRecordFile("ag")}", $"seed {kit.Layout.SeedFile("ag")}", $"log {kit.Layout.LogFile("ag")}"], left.Details);
        Assert.True(Directory.Exists(worktree));
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// A removal that stopped part way can take the worktree's own ignore rules with it; a file the main tree ignores is
    /// still never taken for work done since the closing, so deleting again finishes.
    /// </summary>
    [Fact]
    public async Task AClosedAgentWhoseIgnoreRulesAreGone_TakesNoFileTheMainTreeIgnoresForWork()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        var agents = kit.Harness.Agents(new LateEvidenceFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "evidence", "late.log")), kit.Harness.AnchorRegistryService);
        Assert.Equal(HarnessExit.Incomplete, (await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token)).ExitCode);
        File.Delete(Path.Combine(worktree, ".gitignore"));

        var finished = await kit.DeleteAsync("ag", apply: true);

        Assert.True(finished.Succeeded, OrchestrationKit.Describe(finished));
        Assert.Equal(AgentStates.Deleted, kit.Record("ag").State);
        Assert.False(Directory.Exists(worktree));
    }

    /// <summary>
    /// A closed agent whose worktree is no longer one - no .git of its own, as a removal that stopped part way leaves it -
    /// has its evidence kept again and is never forced: the removal that would force it is named for a person. Its dry run
    /// answers as every --apply of it does - exit 21, naming the same removal - and keeps no evidence.
    /// </summary>
    [Fact]
    public async Task AClosedAgentsHusk_IsNeverForced()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        var agents = kit.Harness.Agents(new LateEvidenceFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "evidence", "late.log")), kit.Harness.AnchorRegistryService);
        Assert.Equal(HarnessExit.Incomplete, (await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token)).ExitCode);
        File.Delete(Path.Combine(worktree, ".git"));
        var kept = Directory.GetDirectories(kit.Layout.EvidenceDirectory("ag")).Length;

        var dry = await kit.DeleteAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Incomplete, dry.ExitCode);
        Assert.Contains("is no longer a git worktree - it holds no .git of its own", dry.Message);
        Assert.Contains("--apply keeps its evidence again and cannot finish the removal", dry.Message);
        Assert.Contains($"'{Core.Hosts.ToolPackage.Command} delete-worktree o1/ag --force' removes it - nothing here forces it -", dry.Message);
        Assert.Equal(kept, Directory.GetDirectories(kit.Layout.EvidenceDirectory("ag")).Length);

        var left = await kit.DeleteAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Incomplete, left.ExitCode);
        Assert.Contains("is no longer a git worktree - it holds no .git of its own", left.Message);
        Assert.Contains($"'{Core.Hosts.ToolPackage.Command} delete-worktree o1/ag --force' removes it - nothing here forces it -", left.Message);
        Assert.True(Directory.Exists(worktree));
        Assert.Equal(AgentStates.Closed, kit.Record("ag").State);
    }

    /// <summary>A worktree made at a closed agent's path since is another worktree, and is not the agent's to remove.</summary>
    [Fact]
    public async Task ANewWorktreeAtAClosedAgentsPath_IsNotItsToRemove()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        var agents = kit.Harness.Agents(new LateEvidenceFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "evidence", "late.log")), kit.Harness.AnchorRegistryService);
        Assert.Equal(HarnessExit.Incomplete, (await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token)).ExitCode);
        Assert.True((await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "o1/ag", force: true, deleteEvidence: true, cancellationToken: Token)).Succeeded);
        Assert.True((await kit.Harness.WorktreeService.CreateAtAsync(kit.Main, WorktreeAddress.Nested("o1", "ag"), Token)).Succeeded);

        var refused = await kit.DeleteAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("is not the worktree closed then: git knows it as another, made there since", refused.Message);
        Assert.True(Directory.Exists(worktree));
    }

    /// <summary>
    /// The proof that nothing is left to fold counts a path the fold would still hand: a path settled by hand, which its
    /// apply hands, that the main tree moves again between the fold's writing and the proof stops the deletion, exit 21,
    /// before anything is removed - never "nothing left" while a hand-over is still to come.
    /// </summary>
    [Fact]
    public async Task APathTheDeletionWouldStillHand_StopsIt_BeforeAnythingIsRemoved()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "settled/x.md", "one\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a file to reconcile by hand", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "settled/x.md", "one\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "settled/x.md", "one\nreconciled by hand\n");

        // The main tree moves the settled path between the fold's write and the proof that asks what is left.
        var agents = kit.Harness.Agents(
            new ChangedOnSeedWriteFileSystem(kit.Harness.FileSystem, kit.Layout.SeedFile("ag"), Path.Combine(kit.Main, "settled", "x.md")),
            kit.Harness.AnchorRegistryService);

        var stopped = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances { Settled = ["settled/x.md"] }, apply: true, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' was not deleted: after its fold was written, it still differs from the main tree - settled/x.md.", stopped.Message);
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
        Assert.True(Directory.Exists(worktree));
        Assert.Equal("moved meanwhile\n", OrchestrationKit.Read(kit.Main, "settled/x.md"));
        Assert.Equal("one\nreconciled by hand\n", OrchestrationKit.Read(worktree, "settled/x.md"));
    }

    /// <summary>
    /// A live agent whose worktree's identity cannot be read - git names no commondir in its git directory - fails its
    /// deletion before anything is written, and its dry run fails the same, in the apply's words: which git worktree it is
    /// is what an --apply reads first, and a worktree whose identity cannot be read is never closed.
    /// </summary>
    [Fact]
    public async Task ALiveAgentWhoseWorktreeCannotBeIdentified_FailsItsDryRunAsItsDeletion()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var commondir = Path.Combine(Directory.GetDirectories(Path.Combine(kit.Main, ".git", "worktrees")).Single(), "commondir");
        var agents = kit.Harness.Agents(new UnknowableFileSystem(kit.Harness.FileSystem, commondir), kit.Harness.AnchorRegistryService);

        var dry = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token);
        var failed = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.CommandFailed, dry.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' was not deleted: which git worktree", dry.Message);
        Assert.Equal(dry.Message, failed.Message);
        Assert.Equal(HarnessExit.CommandFailed, failed.ExitCode);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
    }

    /// <summary>
    /// A live agent whose worktree's identity cannot be read, and that a run also holds the worktree of, fails its dry
    /// run as its deletion does - exit 20, its words unchanged - and names the holder on a line of its own: what cannot
    /// be read stands above a holder in the plan's order.
    /// </summary>
    [Fact]
    public async Task ALiveAgentWhoseWorktreeCannotBeIdentified_AndIsHeld_FailsItsDryRunNamingTheHolder()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var commondir = Path.Combine(Directory.GetDirectories(Path.Combine(kit.Main, ".git", "worktrees")).Single(), "commondir");
        var agents = kit.Harness.Agents(new UnknowableFileSystem(kit.Harness.FileSystem, commondir), kit.Harness.AnchorRegistryService);

        await using var held = await HoldTheWorktreeAsync(kit, worktree);

        var dry = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.CommandFailed, dry.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' was not deleted: which git worktree", dry.Message);
        Assert.DoesNotContain("a run also holds", dry.Message, StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.StartsWith($"held now: '{worktree}' on local is held by ", StringComparison.Ordinal));
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
    }

    /// <summary>
    /// A dry run of a deletion exits 0 exactly where the --apply typed after it, nothing having moved, exits 0; and where
    /// the agent's worktree is gone and nothing is folded, both refuse it until --discard-uncommitted is given, whose dry
    /// run then exits 0 as its apply goes through.
    /// </summary>
    [Fact]
    public async Task ADryRunOfADeletion_ExitsExactlyAsItsApplyWould()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");

        var dry = await kit.DeleteAsync("ag", apply: false);
        var deleted = await kit.DeleteAsync("ag", apply: true);
        await kit.CreateAgentAsync("ag2");
        Assert.True((await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "o1/ag2", force: true, deleteEvidence: true, cancellationToken: Token)).Succeeded);
        var gone = await kit.DeleteAsync("ag2", apply: false);
        var abandoning = await kit.DeleteAsync("ag2", apply: false, discard: true);
        var abandoned = await kit.DeleteAsync("ag2", apply: true, discard: true);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.False(Directory.Exists(worktree));
        Assert.Equal(HarnessExit.Refused, gone.ExitCode);
        Assert.StartsWith("Agent 'ag2' of 'o1' has no worktree at", gone.Message);
        Assert.True(abandoning.Succeeded, OrchestrationKit.Describe(abandoning));
        Assert.True(abandoned.Succeeded, OrchestrationKit.Describe(abandoned));
        Assert.Equal(AgentStates.Deleted, kit.Record("ag2").State);
    }

    /// <summary>
    /// A closed agent's dry run asks the copy questions its --apply's removal asks, whatever the agent's state: a run
    /// holding a copy a host keeps of the worktree is named on a <c>held now:</c> line, and the dry run is unfinished -
    /// exit 21 - as its --apply is, which keeps its evidence again and could not finish the removal. With
    /// <c>--json</c>, the copy is in <c>heldNow</c> with <c>holds</c> <c>"copy"</c>.
    /// </summary>
    [Fact]
    public async Task AClosedAgentsDryRun_NamesAHostCopyARunHolds_AndIsUnfinished()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        var copy = "/home/pi/repo--wt--o1--ag";
        var agents = kit.Harness.Agents(
            new LateEvidenceFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "evidence", "late.log")),
            kit.Harness.AnchorRegistryService,
            hostCopies: new HostCopyRemover(new NoHosts(), new NoHosts(), kit.Harness.RunLock, kit.Harness.FileSystem, kit.Harness.Platform));
        Assert.Equal(HarnessExit.Incomplete, (await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token)).ExitCode);
        Assert.Equal(AgentStates.Closed, kit.Record("ag").State);

        new HostCopyRecord(kit.Harness.FileSystem, kit.Harness.Platform.PathComparison)
            .Claim(new HarnessLayout(kit.Main, kit.Main), new HostCopyEntry(WorktreeAddress.Nested("o1", "ag").CopyName, "ssh pi", copy, worktree));

        var attempt = await kit.Harness.RunLock.TryAcquireAsync(
            new HarnessLayout(kit.Main, kit.Main),
            new LockRequest { Host = "ssh pi", Tree = copy, Scope = LockScope.TreeExclusive, RunId = RunId.New(), Command = "test" },
            Token);

        await using var held = attempt.Handle ?? throw new InvalidOperationException($"the test could not hold '{copy}': {attempt.HeldBy}");

        var dry = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Incomplete, dry.ExitCode);
        Assert.EndsWith(TreeHolders.RemovalHeld, dry.Message, StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.StartsWith($"held now: '{copy}' on ssh pi is held by ", StringComparison.Ordinal));

        var answered = await CliRunner.RunAsync(["delete-agent", "o1", "ag", "--json"], Token, workingDirectory: kit.Main);

        Assert.Equal(HarnessExit.Incomplete, answered.ExitCode);
        var holder = JsonNode.Parse(answered.StandardOutput.TrimEnd())!["heldNow"]!.AsArray().Single()!;
        Assert.Equal("copy", holder["holds"]!.GetValue<string>());
        Assert.Equal(copy, holder["path"]!.GetValue<string>());
        Assert.Equal("ssh pi", holder["host"]!.GetValue<string>());
    }

    /// <summary>
    /// A closed agent's --apply takes its worktree - there, or gone since the closing - before it keeps anything again, so
    /// its dry run asks the lock for it: a run holding it refuses the dry run, exit 13, saying nothing in the request needs
    /// correcting, and is named on a <c>held now:</c> line.
    /// </summary>
    [Fact]
    public async Task AClosedAgentsDryRun_IsRefusedWhileARunHoldsItsWorktree_ThereOrGone()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        var agents = kit.Harness.Agents(new LateEvidenceFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "evidence", "late.log")), kit.Harness.AnchorRegistryService);
        Assert.Equal(HarnessExit.Incomplete, (await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token)).ExitCode);
        Assert.Equal(AgentStates.Closed, kit.Record("ag").State);

        await using (await HoldTheWorktreeAsync(kit, worktree))
        {
            var there = await kit.DeleteAsync("ag", apply: false);

            Assert.Equal(HarnessExit.Refused, there.ExitCode);
            Assert.EndsWith(TreeHolders.Blocked, there.Message, StringComparison.Ordinal);
            Assert.Contains(there.Details!, line => line.StartsWith($"held now: '{worktree}' on local is held by ", StringComparison.Ordinal));
        }

        Assert.True((await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "o1/ag", force: true, deleteEvidence: true, cancellationToken: Token)).Succeeded);

        await using (await HoldTheWorktreeAsync(kit, worktree))
        {
            var gone = await kit.DeleteAsync("ag", apply: false);

            Assert.Equal(HarnessExit.Refused, gone.ExitCode);
            Assert.EndsWith(TreeHolders.Blocked, gone.Message, StringComparison.Ordinal);
            Assert.Contains(gone.Details!, line => line.StartsWith($"held now: '{worktree}' on local is held by ", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A mutation worker a sweep still running holds is named by a dry run of the deletion, in its family's own words,
    /// and the dry run is unfinished - exit 21 - as its --apply is, which folds and closes and cannot finish the
    /// removal. With <c>--json</c> the sweep is in <c>heldNow</c>, as every holder that decides the code is:
    /// <c>holds</c> <c>copy</c>, the worker's path on this machine, its process, its run and since when as its claim
    /// records them, and <c>said</c> the line its family says of it - its claim names it, not the run lock, so only what
    /// a claim does not record, the command, the variant and where a leg stands, is null.
    /// </summary>
    [Fact]
    public async Task ADryRunOfADeletionNamesAMutationWorkerASweepHolds_AndIsUnfinished()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var worker = MutationWorkers.PathOf(worktree, new VariantKey("x86_64", "gcc", "debug", null), 1);
        await kit.Harness.LocalTransport.CreateRootAsync(worker, CopyMark.Complete, Token);
        var sweep = RunId.New();
        var claims = new DirectoryClaims(kit.Harness.FileSystem, kit.Harness.Output, kit.Harness.Identity, MutationWorkers.Claims("check-mutations"));
        claims.Claim(worker, sweep, force: false);
        var claim = claims.Owner(worker)!;

        var dry = await kit.DeleteAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Incomplete, dry.ExitCode);
        Assert.EndsWith(TreeHolders.RemovalHeld, dry.Message, StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.StartsWith($"left the mutation worker '{worker}' kept in it: a sweep still running holds it: ", StringComparison.Ordinal));

        var answered = await CliRunner.RunAsync(["delete-agent", "o1", "ag", "--json"], Token, workingDirectory: kit.Main);

        Assert.Equal(HarnessExit.Incomplete, answered.ExitCode);
        var document = JsonNode.Parse(answered.StandardOutput.TrimEnd())!;
        Assert.Equal("sound", document["request"]!.GetValue<string>());
        var holder = document["heldNow"]!.AsArray().Single()!;
        Assert.Equal("copy", holder["holds"]!.GetValue<string>());
        Assert.Equal(worker, holder["path"]!.GetValue<string>());
        Assert.Equal("local", holder["host"]!.GetValue<string>());
        Assert.Equal(kit.Harness.Identity.CurrentId, holder["processId"]!.GetValue<int>());
        Assert.Equal(sweep.Value, holder["run"]!.GetValue<string>());
        Assert.Equal(claim.TakenUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), holder["since"]!.GetValue<string>());
        Assert.Null(holder["variant"]);
        Assert.Null(holder["command"]);
        Assert.Null(holder["standing"]);
        Assert.Null(holder["ahead"]);
        var said = holder["said"]!.GetValue<string>();
        Assert.StartsWith($"left the mutation worker '{worker}' kept in it: a sweep still running holds it: ", said, StringComparison.Ordinal);
        Assert.Contains($"run {sweep.Value}", said, StringComparison.Ordinal);
        Assert.Contains(said, document["lines"]!.AsArray().Select(line => line!.GetValue<string>()));

        var applied = await kit.DeleteAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Incomplete, applied.ExitCode);
        Assert.Equal(AgentStates.Closed, kit.Record("ag").State);
        Assert.True(Directory.Exists(worker));
    }

    /// <summary>
    /// A directory in the worktree's slot for leg copies, or for mutation workers, that no sync made is nobody's the
    /// harness may remove and keeps nothing: the --apply deletes it with the worktree. So the dry run says it in its
    /// family's words and holds nothing over it - exit 0, as its --apply's is, which then deletes the agent and the
    /// worktree with both directories in it, each said to have gone with it as what its family calls one.
    /// </summary>
    [Fact]
    public async Task DirectoriesNoSyncMadeInTheWorktreesSlots_AreOnlySaid_AndItsDryRunExitsAsItsApplyDoes()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var variant = new VariantKey("x86_64", "gcc", "debug", null);
        var legCopy = LegCopies.PathOf(worktree, variant);
        var worker = MutationWorkers.PathOf(worktree, variant, 1);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(legCopy).FullName, "main.c"), "int main;");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(worker).FullName, "main.c"), "int main;");

        var dry = await kit.DeleteAsync("ag", apply: false);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Contains(dry.Details!, line => line.StartsWith($"left '{legCopy}', named as a leg copy kept in it: ", StringComparison.Ordinal));
        Assert.Contains(dry.Details!, line => line.StartsWith($"left '{worker}', named as a mutation worker kept in it: ", StringComparison.Ordinal));

        var answered = await CliRunner.RunAsync(["delete-agent", "o1", "ag", "--json"], Token, workingDirectory: kit.Main);

        Assert.Equal(0, answered.ExitCode);
        Assert.Empty(JsonNode.Parse(answered.StandardOutput.TrimEnd())!["heldNow"]!.AsArray());

        var deleted = await kit.DeleteAsync("ag", apply: true);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Equal(AgentStates.Deleted, kit.Record("ag").State);
        Assert.False(Directory.Exists(worktree));
        Assert.Contains($"  '{legCopy}', named as a leg copy though nothing says the harness made it, went with it", deleted.Details!);
        Assert.Contains($"  '{worker}', named as a mutation worker though nothing says the harness made it, went with it", deleted.Details!);
    }

    /// <summary>
    /// Copies kept in the worktree that cannot be looked for - its leg copies, or its mutation workers - keep every
    /// --apply's removal from finishing until that is put right: the --apply folds and closes the agent and leaves the
    /// removal, exit 21. Its dry run answers as that --apply does - exit 21, its request refused, its last line saying the
    /// removal could not be finished over what is said above - and names no holder, since none holds anything: never the
    /// words that a run or a sweep holds a copy.
    /// </summary>
    /// <param name="slotName">The slot that cannot be listed.</param>
    /// <param name="said">How the dry run's line about it starts.</param>
    [Theory]
    [InlineData(HarnessLayout.LegsDirectoryName, "the leg copies kept in it could not be removed: ")]
    [InlineData(HarnessLayout.MutationsDirectoryName, "the mutation workers kept in it could not be removed: ")]
    public async Task CopiesKeptInTheWorktreeThatCannotBeLookedFor_LeaveTheRemovalUnfinished_InTheDryRunAsInTheApply(string slotName, string said)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var slot = Directory.CreateDirectory(Path.Combine(worktree, slotName)).FullName;
        var agents = kit.Harness.Agents(new CannotList(kit.Harness.FileSystem, slot, denied: false), kit.Harness.AnchorRegistryService);

        var dry = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Incomplete, dry.ExitCode);
        Assert.StartsWith("dry run: deleting agent 'ag' of 'o1' folds 1 path(s)", dry.Message, StringComparison.Ordinal);
        Assert.EndsWith(Unremovable, dry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("holds a copy", dry.Message, StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.StartsWith(said, StringComparison.Ordinal));
        Assert.DoesNotContain(dry.Details!, line => line.StartsWith("held now:", StringComparison.Ordinal));

        var document = JsonNode.Parse((await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token, json: true)).Data[0])!;

        Assert.Equal(HarnessExit.Incomplete, document["exitCode"]!.GetValue<int>());
        Assert.Equal("refused", document["request"]!.GetValue<string>());
        Assert.Empty(document["heldNow"]!.AsArray());

        var applied = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Incomplete, applied.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' is closed and not deleted yet: ", applied.Message, StringComparison.Ordinal);
        Assert.Equal(AgentStates.Closed, kit.Record("ag").State);
        Assert.True(Directory.Exists(worktree));
    }

    /// <summary>
    /// A mutation worker whose marker cannot be read is not told from one a sync made, so no removal takes it until it
    /// can be told: the --apply folds and closes the agent and leaves the removal, exit 21. Its dry run answers as that
    /// --apply does - exit 21, its request refused, its last line saying the removal could not be finished over what is
    /// said above - and names no holder: nothing holds the worker that waiting would end.
    /// </summary>
    [Fact]
    public async Task AMutationWorkerWhoseMarkerCannotBeRead_LeavesTheRemovalUnfinished_InTheDryRunAsInTheApply()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var worker = MutationWorkers.PathOf(worktree, new VariantKey("x86_64", "gcc", "debug", null), 1);
        await kit.Harness.LocalTransport.CreateRootAsync(worker, CopyMark.Complete, Token);
        File.WriteAllText(Path.Combine(worker, HarnessLayout.DirectoryName, LocalSyncTransport.MarkerFileName), "{}");

        var dry = await kit.DeleteAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Incomplete, dry.ExitCode);
        Assert.EndsWith(Unremovable, dry.Message, StringComparison.Ordinal);
        Assert.Contains(
            dry.Details!,
            line => line.StartsWith($"left the mutation worker '{worker}' kept in it: ", StringComparison.Ordinal)
                && line.EndsWith("removes it once nothing stops it", StringComparison.Ordinal));
        Assert.DoesNotContain(dry.Details!, line => line.StartsWith("held now:", StringComparison.Ordinal));

        var answered = await CliRunner.RunAsync(["delete-agent", "o1", "ag", "--json"], Token, workingDirectory: kit.Main);

        Assert.Equal(HarnessExit.Incomplete, answered.ExitCode);
        var document = JsonNode.Parse(answered.StandardOutput.TrimEnd())!;
        Assert.Equal("refused", document["request"]!.GetValue<string>());
        Assert.Empty(document["heldNow"]!.AsArray());

        var applied = await kit.DeleteAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Incomplete, applied.ExitCode);
        Assert.Equal(AgentStates.Closed, kit.Record("ag").State);
        Assert.True(Directory.Exists(worker));
    }

    /// <summary>
    /// A dry run of a deletion whose rows need a consent nobody gave is refused for that consent, exit 13, request
    /// needs-consent, whatever is kept in the worktree that the removal could not take: its --apply is refused before it
    /// goes anywhere near the removal, so the dry run never says it would go as far as that.
    /// </summary>
    [Fact]
    public async Task ADryRunOfADeletionThatNeedsAConsent_AndKeepsCopiesItCannotRemove_IsRefusedForTheConsent()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.Harness.AnchorRegistryService.WriteAsync(
            kit.Main,
            new Core.Anchors.AnchorWriteRequest("D-TEST-AGENT-ROW", "P2", "something the agent found") { ClosingWork = "the plan we agreed", CrossRefs = "b.txt" },
            dryRun: false,
            Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        kit.FileRow("ag", "D-TEST-AGENT-ROW", new Dictionary<string, string>(Row) { ["closing"] = "another plan" });
        var slot = Directory.CreateDirectory(Path.Combine(worktree, HarnessLayout.LegsDirectoryName)).FullName;
        var agents = kit.Harness.Agents(new CannotList(kit.Harness.FileSystem, slot, denied: false), kit.Harness.AnchorRegistryService);

        var dry = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.EndsWith("the line 'to do it:' above is the command that does it", dry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("could not finish it", dry.Message, StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.StartsWith("the leg copies kept in it could not be removed: ", StringComparison.Ordinal));

        var document = JsonNode.Parse((await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token, json: true)).Data[0])!;

        Assert.Equal(HarnessExit.Refused, document["exitCode"]!.GetValue<int>());
        Assert.Equal("needs-consent", document["request"]!.GetValue<string>());
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
    }

    /// <summary>
    /// A dry run of a deletion whose rows need a consent nobody gave, and that a run also holds the worktree, is refused
    /// with both said: the consent's own words and lines, and the one clause that a run also holds what --apply needs -
    /// never the words that nothing in the request needs correcting.
    /// </summary>
    [Fact]
    public async Task ADryRunOfADeletionThatNeedsAConsentAndIsHeld_SaysBoth()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.Harness.AnchorRegistryService.WriteAsync(
            kit.Main,
            new Core.Anchors.AnchorWriteRequest("D-TEST-AGENT-ROW", "P2", "something the agent found") { ClosingWork = "the plan we agreed", CrossRefs = "b.txt" },
            dryRun: false,
            Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        kit.FileRow("ag", "D-TEST-AGENT-ROW", new Dictionary<string, string>(Row) { ["closing"] = "another plan" });

        await using var held = await HoldTheWorktreeAsync(kit, worktree);

        var dry = await kit.DeleteAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.EndsWith(TreeHolders.AlsoHeld, dry.Message, StringComparison.Ordinal);
        Assert.EndsWith("the line 'to do it:' above is the command that does it" + TreeHolders.AlsoHeld, dry.Message, StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.StartsWith("to do it: ", StringComparison.Ordinal));
        Assert.Contains(dry.Details!, line => line.StartsWith($"held now: '{worktree}' on local is held by ", StringComparison.Ordinal));
        Assert.DoesNotContain("nothing in this request needs correcting", dry.Message, StringComparison.Ordinal);
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
    }

    /// <summary>
    /// Where the worktrees root is a link - as it is where the worktrees are kept on another disk - a run in the agent's
    /// worktree holds its leg copy as git spells that worktree, every link followed, while the deletion reaches the copy
    /// through the link: the lock keys both spellings alike, so the dry run names the run and is unfinished, exit 21, as
    /// its --apply is, which closes the agent and leaves the copy where the run works in it.
    /// </summary>
    [Fact]
    public async Task ALegCopyARunHoldsThroughALinkedWorktreesRoot_LeavesTheDeletionUnfinished_InTheDryRunAsInTheApply()
    {
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var (kit, worktree) = await ThroughALinkedRootAsync(temp, elsewhere);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var copy = await LegCopyAsync(kit, Path.Combine(worktree, HarnessLayout.LegsDirectoryName, "357e24c"));
        var run = RunId.New();

        await using var held = await HoldTheCopyAsync(kit, kit.Harness.FileSystem.ResolveLinks(copy), run);

        var dry = await kit.DeleteAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Incomplete, dry.ExitCode);
        Assert.EndsWith(TreeHolders.RemovalHeld, dry.Message, StringComparison.Ordinal);
        Assert.Contains(
            dry.Details!,
            line => line.StartsWith($"left the leg copy '{copy}' kept in it: a run still running holds it: ", StringComparison.Ordinal)
                && line.Contains($"run {run.Value}", StringComparison.Ordinal));

        var applied = await kit.DeleteAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Incomplete, applied.ExitCode);
        Assert.Equal(AgentStates.Closed, kit.Record("ag").State);
        Assert.True(File.Exists(Path.Combine(copy, "main.c")), "the copy a run works in was removed");
    }

    /// <summary>
    /// delete-worktree asks the lock for a leg copy kept in the worktree as the lock keys it, links followed: where the
    /// worktrees root is a link and a run holds the copy as git spells the worktree, the deletion is refused, exit 13,
    /// naming the run, and removes nothing.
    /// </summary>
    [Fact]
    public async Task DeleteWorktree_IsRefusedForALegCopyARunHoldsThroughALinkedWorktreesRoot()
    {
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var (kit, worktree) = await ThroughALinkedRootAsync(temp, elsewhere);
        var copy = await LegCopyAsync(kit, Path.Combine(worktree, HarnessLayout.LegsDirectoryName, "357e24c"));
        var run = RunId.New();

        await using var held = await HoldTheCopyAsync(kit, kit.Harness.FileSystem.ResolveLinks(copy), run);

        var refused = await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "o1/ag", force: false, deleteEvidence: true, cancellationToken: Token);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.StartsWith(
            $"Worktree 'o1/ag' was not deleted: a leg copy kept in it is in use - '{copy}': a run still running holds it: ",
            refused.Outcome.Message,
            StringComparison.Ordinal);
        Assert.Contains($"run {run.Value}", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(copy, "main.c")), "the copy a run works in was removed");
        Assert.True(Directory.Exists(worktree));
    }

    /// <summary>
    /// A sweep this machine runs on a host holds its key in the host's copy of the worktree - one more copy in that copy's
    /// .mutations - and removing the copy takes everything in it: the dry run names the sweep on a <c>held now:</c> line
    /// and is unfinished, exit 21, as its --apply is, which closes the agent and leaves the copy recorded without asking
    /// the host to remove a copy the sweep works in. With <c>--json</c> the sweep is in <c>heldNow</c>, holding a copy.
    /// </summary>
    [Fact]
    public async Task ASweepInAHostsCopyOfTheWorktree_LeavesTheDeletionUnfinished_InTheDryRunAsInTheApply()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(
            temp,
            hosts: new Core.Configuration.HostsConfig { Ssh = { ["pi"] = new Core.Configuration.SshHostConfig { RepositoryPath = "/home/pi/repo" } } });
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var copy = "/home/pi/repo" + HostCopies.WorktreeSuffix + WorktreeAddress.Nested("o1", "ag").CopyName;
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var record = new HostCopyRecord(kit.Harness.FileSystem, kit.Harness.Platform.PathComparison);
        record.Claim(layout, new HostCopyEntry(WorktreeAddress.Nested("o1", "ag").CopyName, "ssh pi", copy, worktree));
        var agents = kit.Harness.Agents(
            kit.Harness.FileSystem,
            kit.Harness.AnchorRegistryService,
            hostCopies: new HostCopyRemover(new NoHosts(), new NoHosts(), kit.Harness.RunLock, kit.Harness.FileSystem, kit.Harness.Platform));
        var sweep = MutationWorkers.SweepLock(Core.Hosts.HostId.Ssh("pi"), copy, new VariantKey("x86_64", "gcc", "debug", null), RunId.New(), "check-mutations");
        var attempt = await kit.Harness.RunLock.TryAcquireAsync(layout, sweep, Token);

        await using var swept = attempt.Handle ?? throw new InvalidOperationException($"the test could not hold '{sweep.Tree}': {attempt.HeldBy}");

        var dry = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Incomplete, dry.ExitCode);
        Assert.EndsWith(TreeHolders.RemovalHeld, dry.Message, StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.StartsWith($"held now: '{sweep.Tree}' on ssh pi is held by ", StringComparison.Ordinal));

        var document = JsonNode.Parse((await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token, json: true)).Data[0])!;
        var holder = document["heldNow"]!.AsArray().Single()!;

        Assert.Equal("sound", document["request"]!.GetValue<string>());
        Assert.Equal("copy", holder["holds"]!.GetValue<string>());
        Assert.Equal(sweep.Tree, holder["path"]!.GetValue<string>());
        Assert.Equal("ssh pi", holder["host"]!.GetValue<string>());
        Assert.Equal("check-mutations", holder["command"]!.GetValue<string>());

        var applied = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Incomplete, applied.ExitCode);
        Assert.Equal(AgentStates.Closed, kit.Record("ag").State);
        Assert.Contains(applied.Details!, line => line.Contains($"ssh pi: its copy at '{copy}' stays, and is still recorded: '{copy}' on ssh pi is held by ", StringComparison.Ordinal));
        Assert.Single(record.Of(layout, WorktreeAddress.Nested("o1", "ag").CopyName));
    }

    /// <summary>
    /// A record of host copies that cannot be read leaves a deletion unable to tell which copies are its worktree's, so a
    /// live agent's --apply is refused before it writes anything - exit 13, nothing folded, the agent live, its worktree
    /// there - in the words its dry run says, which keeps the lines it weighed.
    /// </summary>
    [Fact]
    public async Task ARecordOfHostCopiesThatCannotBeRead_RefusesALiveAgentsDeletion_BeforeAnythingIsWritten_InTheDryRunAsInTheApply()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var agents = WithHostCopies(kit, kit.Harness.FileSystem);
        UnreadableHostCopies(kit);

        var dry = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token);
        var applied = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' was not deleted: ", dry.Message, StringComparison.Ordinal);
        Assert.EndsWith(". Nothing was removed.", dry.Message, StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.Contains("b.txt", StringComparison.Ordinal));
        Assert.Equal(HarnessExit.Refused, applied.ExitCode);
        Assert.Equal(dry.Message, applied.Message);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(worktree, "b.txt"));
    }

    /// <summary>
    /// A record of host copies that cannot be read refuses a closed agent's --apply before it keeps its evidence again, exit
    /// 13, as its dry run is refused: no removal it would go on to could tell which copies are its worktree's.
    /// </summary>
    [Fact]
    public async Task ARecordOfHostCopiesThatCannotBeRead_RefusesAClosedAgentsDeletion_BeforeItsEvidenceIsKeptAgain()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        var closing = WithHostCopies(kit, new LateEvidenceFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "evidence", "late.log")));
        Assert.Equal(HarnessExit.Incomplete, (await closing.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token)).ExitCode);
        Assert.Equal(AgentStates.Closed, kit.Record("ag").State);
        var agents = WithHostCopies(kit, kit.Harness.FileSystem);
        var kept = Directory.GetDirectories(kit.Layout.EvidenceDirectory("ag"));
        UnreadableHostCopies(kit);

        var dry = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: false, Token);
        var applied = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' was not deleted: ", dry.Message, StringComparison.Ordinal);
        Assert.Equal(HarnessExit.Refused, applied.ExitCode);
        Assert.Equal(dry.Message, applied.Message);
        Assert.Equal(kept, Directory.GetDirectories(kit.Layout.EvidenceDirectory("ag")));
        Assert.True(Directory.Exists(worktree));
    }

    /// <summary>
    /// A record of host copies that cannot be read refuses the --apply that would delete an agent whose worktree is gone,
    /// exit 13, before anything is written, as its dry run is refused: the agent stays live.
    /// </summary>
    [Fact]
    public async Task ARecordOfHostCopiesThatCannotBeRead_RefusesAGoneAgentsDeletion_InTheDryRunAsInTheApply()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        Assert.True((await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "o1/ag", force: true, deleteEvidence: true, cancellationToken: Token)).Succeeded);
        var agents = WithHostCopies(kit, kit.Harness.FileSystem);
        UnreadableHostCopies(kit);

        var dry = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, discardUncommitted: true, Token);
        var applied = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: true, Token);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' was not deleted: ", dry.Message, StringComparison.Ordinal);
        Assert.Equal(HarnessExit.Refused, applied.ExitCode);
        Assert.Equal(dry.Message, applied.Message);
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
    }

    /// <summary>The kit's agents over <paramref name="fileSystem"/>, removing the copies hosts keep as a deletion does, asking no host anything.</summary>
    private static AgentService WithHostCopies(OrchestrationKit kit, IFileSystem fileSystem)
        => kit.Harness.Agents(
            fileSystem,
            kit.Harness.AnchorRegistryService,
            hostCopies: new HostCopyRemover(new NoHosts(), new NoHosts(), kit.Harness.RunLock, kit.Harness.FileSystem, kit.Harness.Platform));

    /// <summary>Leaves the kit's record of host copies a file that does not read as one.</summary>
    private static void UnreadableHostCopies(OrchestrationKit kit)
    {
        var record = HostCopyRecord.PathOf(new HarnessLayout(kit.Main, kit.Main));
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        File.WriteAllText(record, "not json");
    }

    /// <summary>
    /// A kit whose worktrees root is a link - a junction on Windows - to a directory in <paramref name="elsewhere"/>, as
    /// worktrees kept on another disk are, with agent <c>ag</c> made through it; and its worktree, spelled through the link.
    /// </summary>
    private static async Task<(OrchestrationKit Kit, string Worktree)> ThroughALinkedRootAsync(TempDirectory temp, TempDirectory elsewhere)
    {
        var kit = await OrchestrationKit.PrepareAsync(temp, settings: OrchestrationKit.SettingsWith(root: "wt-link"));
        var reached = elsewhere.Combine("wt-real");
        Directory.Move(Path.Combine(kit.Main, "wt-link"), reached);
        TestLinks.DirectoryLink(Path.Combine(kit.Main, "wt-link"), reached);
        await kit.CreateAgentAsync("ag");

        return (kit, WorktreeAddress.Nested(OrchestrationKit.Orchestrator, "ag").PathUnder(Path.Combine(kit.Main, "wt-link")));
    }

    /// <summary>A leg's copy of a worktree at <paramref name="path"/>, as a sync makes one.</summary>
    private static async Task<string> LegCopyAsync(OrchestrationKit kit, string path)
    {
        await kit.Harness.LocalTransport.CreateRootAsync(path, CopyMark.Complete, Token);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        File.WriteAllText(Path.Combine(path, "main.c"), "int main;");

        return path;
    }

    /// <summary>Holds the leg copy at <paramref name="copy"/> for one variant of run <paramref name="run"/>, as a leg working in it does.</summary>
    private static async Task<RunLockHandle> HoldTheCopyAsync(OrchestrationKit kit, string copy, RunId run)
    {
        var attempt = await kit.Harness.RunLock.TryAcquireAsync(
            new HarnessLayout(kit.Main, kit.Main),
            new LockRequest { Host = "local", Tree = copy, Variant = "debug", Scope = LockScope.TreeShared, RunId = run, Command = "test" },
            Token);

        return attempt.Handle ?? throw new InvalidOperationException($"the test could not hold '{copy}': {attempt.HeldBy}");
    }

    /// <summary>Takes the worktree at <paramref name="path"/> whole for a run of the test, for as long as the caller holds the attempt.</summary>
    private static async Task<RunLockHandle> HoldTheWorktreeAsync(OrchestrationKit kit, string path)
    {
        var attempt = await kit.Harness.RunLock
            .TryAcquireAsync(new HarnessLayout(kit.Main, kit.Main), new LockRequest { Host = "local", Tree = path, Scope = LockScope.TreeExclusive, RunId = RunId.New(), Command = "test" }, Token);

        return attempt.Handle ?? throw new InvalidOperationException($"the test could not hold '{path}': {attempt.HeldBy}");
    }

    /// <summary>
    /// Evidence is found as a removal reaches it: a link under an evidence root is never followed, so what it leads to is
    /// neither kept nor deleted.
    /// </summary>
    [Fact]
    public async Task Evidence_IsFoundWithoutFollowingALinkUnderAnEvidenceRoot()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        outside.WriteFile("elsewhere.log", "not the agent's\n");
        TestLinks.DirectoryLink(Path.Combine(worktree, "evidence", "linked"), outside.Path);

        var deleted = await kit.DeleteAsync("ag", apply: true, discard: true);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        var kept = Assert.Single(Directory.GetDirectories(kit.Layout.EvidenceDirectory("ag")));
        Assert.Equal([Path.Combine(kept, "evidence", "run.log")], Directory.GetFiles(kept, "*", SearchOption.AllDirectories));
        Assert.Equal("not the agent's\n", File.ReadAllText(outside.Combine("elsewhere.log")));
    }

    /// <summary>A deletion started from inside the agent's worktree is refused - its dry run too, in the same words - reading nothing: the removal cannot take a directory this process stands in.</summary>
    [Fact]
    public async Task DeletingFromInsideTheAgentsWorktree_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        kit.Harness.CurrentDirectory = Path.Combine(worktree, "docs");

        var dry = await kit.DeleteAsync("ag", apply: false, discard: true);
        var refused = await kit.DeleteAsync("ag", apply: true, discard: true);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.StartsWith("This process's working directory", dry.Message);
        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith("This process's working directory", refused.Message);
        Assert.True(Directory.Exists(worktree));
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
    }

    /// <summary>
    /// An agent whose worktree is gone has nothing to fold, so deleting it needs --discard-uncommitted, which closes its
    /// record once git's record of the worktree is gone too; a deleted agent is not deleted again.
    /// </summary>
    [Fact]
    public async Task AnAgentWhoseWorktreeIsGone_IsDeletedOnlyWithNothingFolded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        Assert.True((await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "o1/ag", force: true, deleteEvidence: true, cancellationToken: Token)).Succeeded);

        var refused = await kit.DeleteAsync("ag", apply: true);
        var deleted = await kit.DeleteAsync("ag", apply: true, discard: true);
        var again = await kit.DeleteAsync("ag", apply: true, discard: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' has no worktree at", refused.Message);
        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.True(kit.Record("ag").Abandoned);
        Assert.Equal(HarnessExit.Refused, again.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' was deleted at", again.Message);
    }

    /// <summary>
    /// On Windows, an agent's worktree holding a directory junction is deleted whole, the junction removed as the link it
    /// is and what it leads to untouched.
    /// </summary>
    [Fact]
    public async Task AnAgentsWorktreeHoldingAJunction_IsDeleted_AndWhatItLeadsToStays()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are Windows'.");

        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        outside.WriteFile("kept.txt", "stays\n");
        TestLinks.Junction(Path.Combine(worktree, "linked"), outside.Path);

        var deleted = await kit.DeleteAsync("ag", apply: true, discard: true);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.False(Directory.Exists(worktree));
        Assert.Equal("stays\n", File.ReadAllText(outside.Combine("kept.txt")));
    }

    /// <summary>An evidence file marked read only is kept, deleted as any other, and the agent with it.</summary>
    [Fact]
    public async Task AReadOnlyEvidenceFile_IsKeptAndDeleted_AndTheAgentWithIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "evidence/pack.idx", "packed\n");
        File.SetAttributes(Path.Combine(worktree, "evidence", "pack.idx"), FileAttributes.ReadOnly);

        var deleted = await kit.DeleteAsync("ag", apply: true, discard: true);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.False(Directory.Exists(worktree));
        Assert.Contains(Directory.GetDirectories(kit.Layout.EvidenceDirectory("ag")), directory => File.Exists(Path.Combine(directory, "evidence", "pack.idx")));
    }

    /// <summary>
    /// A kept evidence file that cannot be deleted is left and named, and the removal - its evidence check kept - stops
    /// on it: the agent is closed, and nothing is lost.
    /// </summary>
    [Fact]
    public async Task AnEvidenceFileThatCannotBeDeleted_IsLeftAndNamed_AndTheRemovalStopsOnIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var held = Path.Combine(worktree, "evidence", "run.log");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        var agents = kit.Harness.Agents(new UndeletableFileSystem(kit.Harness.FileSystem, held), kit.Harness.AnchorRegistryService);

        var stopped = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Contains(stopped.Details!, line => line.StartsWith("1 evidence file(s) were left: evidence/run.log (", StringComparison.Ordinal));
        Assert.Equal(AgentStates.Closed, kit.Record("ag").State);
        Assert.True(File.Exists(held));
    }

    /// <summary>
    /// An evidence root that is a link, or is reached through one, holds nothing a removal would take: it is neither kept
    /// nor counted, what it leads to is left alone, and the removal goes ahead.
    /// </summary>
    [Fact]
    public async Task AnEvidenceRootThatIsOrPassesThroughALink_IsNeitherKeptNorCounted()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp, settings: OrchestrationKit.SettingsWith(evidenceRoots: ["evidence", "deep/logs"]));
        var worktree = await kit.CreateAgentAsync("ag");
        outside.WriteFile("kept.log", "not the agent's\n");
        outside.WriteFile(Path.Combine("logs", "deeper.log"), "not the agent's either\n");
        TestLinks.DirectoryLink(Path.Combine(worktree, "evidence"), outside.Path);
        TestLinks.DirectoryLink(Path.Combine(worktree, "deep"), outside.Path);

        var deleted = await kit.DeleteAsync("ag", apply: true, discard: true);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Contains(deleted.Details!, line => line.StartsWith("its evidence roots held nothing to keep; deep/logs, evidence are links", StringComparison.Ordinal));
        Assert.False(Directory.Exists(worktree));
        Assert.True(File.Exists(outside.Combine("kept.log")));
        Assert.True(File.Exists(outside.Combine("logs", "deeper.log")));
    }

    /// <summary>
    /// A transcript found and not kept stops the deletion before the agent is closed: its worktree stays, and running
    /// delete-agent again tries again.
    /// </summary>
    [Fact]
    public async Task ATranscriptThatCannotBeKept_StopsTheDeletionBeforeTheClosing()
    {
        using var temp = new TempDirectory();
        using var claude = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        kit.Harness.ClaudeConfigDirectory = claude.Path;
        var transcript = claude.WriteFile(Path.Combine("projects", "some-project", "sess1.jsonl"), "{}\n");
        var worktree = await kit.CreateAgentAsync("ag", session: "sess1");
        var agents = kit.Harness.Agents(new UnreadableFileSystem(kit.Harness.FileSystem, transcript), kit.Harness.AnchorRegistryService);

        var stopped = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: true, Token);

        Assert.Equal(HarnessExit.CommandFailed, stopped.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' was not deleted, because its transcripts could not be kept:", stopped.Message);
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
        Assert.True(Directory.Exists(worktree));
    }

    /// <summary>A log that cannot take a line never replaces what the command did: the deletion is still said to be done, and the log's trouble said with it.</summary>
    [Fact]
    public async Task ALogThatCannotTakeALine_NeverReplacesWhatTheCommandDid()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        File.Delete(kit.Layout.LogFile("ag"));
        Directory.CreateDirectory(kit.Layout.LogFile("ag"));

        var deleted = await kit.DeleteAsync("ag", apply: true, discard: true);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Contains(deleted.Details!, line => line.StartsWith($"its log '{kit.Layout.LogFile("ag")}' could not take this line:", StringComparison.Ordinal));
        Assert.Equal(AgentStates.Deleted, kit.Record("ag").State);
    }

    /// <summary>delete-worktree does not refuse over an evidence root that is a link: removing the worktree leaves what it leads to.</summary>
    [Fact]
    public async Task DeleteWorktree_DoesNotRefuseOverAnEvidenceRootThatIsALink()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var created = await kit.Harness.WorktreeService.CreateAsync(kit.Main, "plain", useRandomName: false, Token);
        outside.WriteFile("kept.log", "not the worktree's\n");
        TestLinks.DirectoryLink(Path.Combine(created.Path, "evidence"), outside.Path);

        var deleted = await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "plain", force: false, deleteEvidence: false, discardUncommitted: true, Token);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.True(File.Exists(outside.Combine("kept.log")));
    }

    /// <summary>
    /// The real file system, except that deleting a kept evidence file writes another into its root, as a run still
    /// writing there would between the evidence being read back and the worktree being removed.
    /// </summary>
    /// <summary>
    /// delete-agent holds a row that does not keep its stored text to the fold's checks: its dry run names the command that
    /// deletes it accepting the loss, on the line its summary points at, and exits 13, as the --apply that refuses with
    /// nothing written and the agent still live does; the command it named deletes it.
    /// </summary>
    [Fact]
    public async Task DeletingAnAgent_WhoseRowLosesStoredText_IsRefusedUntilItIsAccepted()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.Harness.AnchorRegistryService.WriteAsync(
            kit.Main,
            new Core.Anchors.AnchorWriteRequest("D-TEST-AGENT-ROW", "P2", "something the agent found") { ClosingWork = "the plan we agreed", CrossRefs = "b.txt" },
            dryRun: false,
            Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        kit.FileRow("ag", "D-TEST-AGENT-ROW", new Dictionary<string, string>(Row) { ["closing"] = "another plan" });

        var dry = await kit.DeleteAsync("ag", apply: false);
        var refused = await kit.DeleteAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.Equal($"to do it: '{Core.Hosts.ToolPackage.Command} delete-agent o1 ag --apply --accept-lost D-TEST-AGENT-ROW:closing'", dry.Details![^1]);
        Assert.EndsWith("the line 'to do it:' above is the command that does it", dry.Message, StringComparison.Ordinal);
        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);

        var deleted = await kit.DeleteAsync("ag", apply: true, new FoldAllowances { AcceptLost = ["D-TEST-AGENT-ROW:closing"] });

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Equal(AgentStates.Deleted, kit.Record("ag").State);
    }

    private sealed class LateEvidenceFileSystem(IFileSystem inner, string lateFile) : PassThroughFileSystem(inner)
    {
        private bool _written;

        public override void DeleteFile(string path)
        {
            base.DeleteFile(path);

            if (!_written && string.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(lateFile), StringComparison.OrdinalIgnoreCase))
            {
                _written = true;
                File.WriteAllText(lateFile, "written late\n");
            }
        }
    }

    /// <summary>
    /// Work the worktree holds once its fold is written - a file an agent still running wrote meanwhile - stops the
    /// deletion before anything is kept or removed: the removal would discard it on a measurement that missed it.
    /// </summary>
    [Fact]
    public async Task WorkFoundAfterTheFoldIsWritten_StopsTheDeletion_BeforeAnythingIsRemoved()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var agents = kit.Harness.Agents(new WritingMeanwhileFileSystem(kit.Harness.FileSystem, Path.Combine(kit.Main, "b.txt"), Path.Combine(worktree, "late.txt")), kit.Harness.AnchorRegistryService);

        var stopped = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Contains("after its fold was written, it still differs from the main tree", stopped.Message);
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
        Assert.True(File.Exists(Path.Combine(worktree, "late.txt")));
    }

    /// <summary>A file the closing recorded and the worktree changed since is work done in an agent already closed: left for a person.</summary>
    [Fact]
    public async Task AClosedAgent_WithARecordedFileChangedSince_IsLeftForAPerson()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        var agents = kit.Harness.Agents(new LateEvidenceFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "evidence", "late.log")), kit.Harness.AnchorRegistryService);
        Assert.Equal(HarnessExit.Incomplete, (await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token)).ExitCode);
        Assert.Contains("b.txt", kit.Record("ag").Closing!.Held.Keys);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\nand more after the closing\n");

        var left = await kit.DeleteAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Incomplete, left.ExitCode);
        Assert.Contains("its worktree holds 1 file(s) its closing did not record", left.Message);
        Assert.Contains("  b.txt", left.Details!);
        Assert.True(Directory.Exists(worktree));
    }

    /// <summary>A closed agent whose worktree is gone - removed after the closing, its record not yet updated - is finished: nothing is left to remove.</summary>
    [Fact]
    public async Task AClosedAgentWhoseWorktreeIsGone_IsFinished()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        var agents = kit.Harness.Agents(new LateEvidenceFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "evidence", "late.log")), kit.Harness.AnchorRegistryService);
        Assert.Equal(HarnessExit.Incomplete, (await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token)).ExitCode);
        Assert.True((await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "o1/ag", force: true, deleteEvidence: true, cancellationToken: Token)).Succeeded);

        var finished = await kit.DeleteAsync("ag", apply: true);

        Assert.True(finished.Succeeded, OrchestrationKit.Describe(finished));
        Assert.Equal(AgentStates.Deleted, kit.Record("ag").State);
        Assert.NotNull(kit.Record("ag").Closing);
    }

    /// <summary>
    /// An evidence file changed after it was kept is not the file kept: it is left and named, and the removal stops on it,
    /// losing nothing.
    /// </summary>
    [Fact]
    public async Task AnEvidenceFileChangedAfterItWasKept_IsLeftAndNamed()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var log = Path.Combine(worktree, "evidence", "run.log");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        var agents = kit.Harness.Agents(new ChangedOnClosingFileSystem(kit.Harness.FileSystem, kit.Layout.AgentRecordFile("ag"), log), kit.Harness.AnchorRegistryService);

        var stopped = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Contains(stopped.Details!, line => line.StartsWith("1 evidence file(s) were left: evidence/run.log (changed since it was kept)", StringComparison.Ordinal));
        Assert.Equal("measured\nwritten after the keeping\n", File.ReadAllText(log));
        Assert.Equal(AgentStates.Closed, kit.Record("ag").State);
    }

    /// <summary>A link to a file under an evidence root is neither kept nor counted: removing the worktree takes the link, never what it leads to.</summary>
    [Fact]
    public async Task ALinkToAFileUnderAnEvidenceRoot_IsNeitherKeptNorCounted()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        TestLinks.OrSkip(() => File.CreateSymbolicLink(Path.Combine(worktree, "evidence", "latest.log"), Path.Combine(worktree, "evidence", "run.log")));

        var deleted = await kit.DeleteAsync("ag", apply: true);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Contains(deleted.Details!, line => line.StartsWith("kept 1 evidence file(s)", StringComparison.Ordinal) && line.Contains("evidence/latest.log is a link", StringComparison.Ordinal));
        var kept = Assert.Single(Directory.GetDirectories(kit.Layout.EvidenceDirectory("ag")));
        Assert.False(File.Exists(Path.Combine(kept, "evidence", "latest.log")));
        Assert.False(Directory.Exists(worktree));
    }

    /// <summary>
    /// What a removal leaves of an agent's worktree - no .git of its own - holding a repository of its own is deleted with
    /// --force: an agent's worktree holds no worktrees below it, so nothing in it is taken for one.
    /// </summary>
    [Fact]
    public async Task AnAgentsHuskHoldingARepository_IsDeletedWithForce()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        await kit.Harness.InitializeGitRepositoryAsync(Directory.CreateDirectory(Path.Combine(worktree, "nested")).FullName, Token);
        File.Delete(Path.Combine(worktree, ".git"));

        var removed = await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "o1/ag", force: true, deleteEvidence: true, cancellationToken: Token);

        Assert.True(removed.Succeeded, removed.Outcome.Message);
        Assert.False(Directory.Exists(worktree));
    }

    /// <summary>The real file system, except that writing one main-tree file writes a file into the agent's worktree too, as an agent still at work would.</summary>
    private sealed class WritingMeanwhileFileSystem(IFileSystem inner, string trigger, string meanwhile) : PassThroughFileSystem(inner)
    {
        public override void ReplaceFile(string source, string destination)
        {
            base.ReplaceFile(source, destination);

            if (string.Equals(Path.GetFullPath(destination), Path.GetFullPath(trigger), StringComparison.OrdinalIgnoreCase) && !File.Exists(meanwhile))
            {
                File.WriteAllText(meanwhile, "written meanwhile\n");
            }
        }
    }

    /// <summary>The real file system, except that recording the agent closed changes one file, as a program still writing it would.</summary>
    private sealed class ChangedOnClosingFileSystem(IFileSystem inner, string record, string changed) : PassThroughFileSystem(inner)
    {
        public override void WriteAllTextAtomic(string path, string contents)
        {
            base.WriteAllTextAtomic(path, contents);

            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(record), StringComparison.OrdinalIgnoreCase)
                && contents.Contains($"\"state\": \"{AgentStates.Closed}\"", StringComparison.Ordinal))
            {
                File.AppendAllText(changed, "written after the keeping\n");
            }
        }
    }

    /// <summary>The real file system, except that one file cannot be deleted, as while another program holds it open.</summary>
    private sealed class UndeletableFileSystem(IFileSystem inner, string held) : PassThroughFileSystem(inner)
    {
        public override void DeleteFile(string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(held), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
            }

            base.DeleteFile(path);
        }
    }

    /// <summary>The real file system, except that one file cannot be read, as while another program holds it without sharing.</summary>
    private sealed class UnreadableFileSystem(IFileSystem inner, string held) : PassThroughFileSystem(inner)
    {
        public override Stream OpenRead(string path)
            => string.Equals(Path.GetFullPath(path), Path.GetFullPath(held), StringComparison.OrdinalIgnoreCase)
                ? throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.")
                : base.OpenRead(path);
    }

    /// <summary>The real file system, except that one file's kind cannot be told, as while another program holds it without sharing.</summary>
    private sealed class UnknowableFileSystem(IFileSystem inner, string held) : PassThroughFileSystem(inner)
    {
        public override PathKind KindOf(string path)
            => string.Equals(Path.GetFullPath(path), Path.GetFullPath(held), StringComparison.OrdinalIgnoreCase)
                ? throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.")
                : base.KindOf(path);
    }

    /// <summary>
    /// The real file system, except that once the agent's seed is written, one main-tree file is changed, as a person or
    /// another run would change it between a fold's write and the proof that asks what is left.
    /// </summary>
    private sealed class ChangedOnSeedWriteFileSystem(IFileSystem inner, string seed, string changed) : PassThroughFileSystem(inner)
    {
        public override void WriteAllTextAtomic(string path, string contents)
        {
            base.WriteAllTextAtomic(path, contents);

            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(seed), StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(changed, "moved meanwhile\n");
            }
        }
    }
}
