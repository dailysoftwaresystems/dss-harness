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

        var dry = await kit.DeleteAsync("ag", apply: false);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.StartsWith("dry run: deleting agent 'ag' of 'o1' folds 1 path(s), removes 0 and applies 1 row(s), keeps its transcripts and 1 evidence file(s)", dry.Message);
        Assert.True(Directory.Exists(worktree));

        var deleted = await kit.DeleteAsync("ag", apply: true);
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
        Assert.Contains(kit.Harness.OrchestrationLog.Read(kit.Layout.LogFile("ag")), entry => entry.Command == AgentService.DeleteCommand && entry.Outcome == "ok");
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

        var stopped = await agents.DeleteAsync(kit.Main, "o1", "ag", [], apply: true, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' is closed and not deleted yet: ", stopped.Message);
        Assert.Contains("Its fold and rows are in the main tree, and it is closed, so nothing folds it again.", stopped.Message);
        Assert.Equal(AgentStates.Closed, kit.Record("ag").State);
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));

        OrchestrationKit.Write(kit.Main, "b.txt", "two\na sibling's fold since\n");
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
        Assert.Equal(HarnessExit.Incomplete, (await agents.DeleteAsync(kit.Main, "o1", "ag", [], apply: true, discardUncommitted: false, Token)).ExitCode);
        OrchestrationKit.Write(worktree, "b.txt", "two\nwork after the closing\n");

        var left = await kit.DeleteAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Incomplete, left.ExitCode);
        Assert.Contains("its worktree holds 1 file(s) its closing did not record", left.Message);
        Assert.Equal(["  b.txt", $"log {kit.Layout.LogFile("ag")}"], left.Details);
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
        Assert.Equal(HarnessExit.Incomplete, (await agents.DeleteAsync(kit.Main, "o1", "ag", [], apply: true, discardUncommitted: false, Token)).ExitCode);
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
        Assert.Equal(HarnessExit.Incomplete, (await agents.DeleteAsync(kit.Main, "o1", "ag", [], apply: true, discardUncommitted: false, Token)).ExitCode);
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
        Assert.Equal(HarnessExit.Incomplete, (await agents.DeleteAsync(kit.Main, "o1", "ag", [], apply: true, discardUncommitted: false, Token)).ExitCode);
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

        var stopped = await agents.DeleteAsync(kit.Main, "o1", "ag", [], apply: true, discardUncommitted: true, Token);

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
        Assert.Contains(deleted.Details!, line => line.StartsWith("its evidence roots held nothing to keep; evidence, deep/logs are links", StringComparison.Ordinal));
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

        var stopped = await agents.DeleteAsync(kit.Main, "o1", "ag", [], apply: true, discardUncommitted: true, Token);

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
