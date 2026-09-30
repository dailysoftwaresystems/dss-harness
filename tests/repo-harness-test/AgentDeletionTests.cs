using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

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
    /// has its evidence kept again and is never forced: the removal that would force it is named for a person.
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

        var left = await kit.DeleteAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Incomplete, left.ExitCode);
        Assert.Contains("is no longer a git worktree - it holds no .git of its own", left.Message);
        Assert.Contains("'dssharness delete-worktree o1/ag --force' removes it - nothing here forces it -", left.Message);
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

    /// <summary>A deletion started from inside the agent's worktree is refused: the removal cannot take a directory this process stands in.</summary>
    [Fact]
    public async Task DeletingFromInsideTheAgentsWorktree_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        kit.Harness.CurrentDirectory = Path.Combine(worktree, "docs");

        var refused = await kit.DeleteAsync("ag", apply: true, discard: true);

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
    /// deletes it accepting the loss, on the line its summary points at, --apply refuses with nothing written and the agent
    /// still live, and the command it named deletes it.
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

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
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
}
