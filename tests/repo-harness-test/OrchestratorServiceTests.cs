using System.Text.Json.Nodes;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>create-orchestrator, delete-orchestrator and list-orchestrator.</summary>
public sealed class OrchestratorServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// An orchestrator is its directory under .orchestrators: its record, logs, work and plans directories, and where its
    /// agents are kept; run again, it changes only its limit and its session, and refuses another model.
    /// </summary>
    [Fact]
    public async Task AnOrchestrator_IsMadeUnderOrchestrators_AndRunAgainChangesOnlyItsLimitAndSession()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var record = kit.Harness.OrchestrationStore.ReadOrchestrator(kit.Layout)!;

        Assert.Equal("model-a", record.Model);
        Assert.Equal(OrchestratorRecord.DefaultParallel, record.Parallel);
        Assert.True(Directory.Exists(kit.Layout.LogsDirectory));
        Assert.True(Directory.Exists(kit.Layout.PlansDirectory("o1")));
        Assert.True(Directory.Exists(kit.Layout.AgentsDirectory));

        var changed = await kit.Harness.OrchestratorService.CreateAsync(kit.Main, "o1", "model-a", 6, "sess-9", Token);
        var model = await kit.Harness.OrchestratorService.CreateAsync(kit.Main, "o1", "model-z", null, null, Token);

        Assert.True(changed.Succeeded, OrchestrationKit.Describe(changed));
        Assert.Equal("orchestrator 'o1' now allows at most 6 agent(s) with a worktree at once, where it allowed 4, and records session sess-9", changed.Message);
        Assert.Equal(6, kit.Harness.OrchestrationStore.ReadOrchestrator(kit.Layout)!.Parallel);
        Assert.Equal(HarnessExit.Refused, model.ExitCode);
    }

    /// <summary>
    /// An orchestrator whose directory git would not ignore is refused: what it keeps is machine-local, and committed it
    /// would travel to every clone.
    /// </summary>
    [Fact]
    public async Task AnOrchestratorGitWouldNotIgnore_IsRefused()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        Directory.CreateDirectory(temp.Combine(".harness-config"));
        harness.WriteConfig(temp.Path, new Core.Configuration.HarnessConfig());

        var refused = await harness.OrchestratorService.CreateAsync(temp.Path, "o1", "model-a", null, null, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith("git does not ignore '.orchestrators/o1/agent.json'", refused.Message);
        Assert.False(Directory.Exists(temp.Combine(".orchestrators", "o1")));
    }

    /// <summary>
    /// Plain worktrees and orchestrators share the names under the worktrees root: an orchestrator is not made over a
    /// worktree of its name.
    /// </summary>
    [Fact]
    public async Task AnOrchestratorNamedAsAWorktree_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        Assert.True((await kit.Harness.WorktreeService.CreateAsync(kit.Main, "o2", useRandomName: false, Token)).Succeeded);

        var refused = await kit.Harness.OrchestratorService.CreateAsync(kit.Main, "o2", "model-a", null, null, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("is a worktree, and an orchestrator's agents' worktrees are made there", refused.Message);
    }

    /// <summary>
    /// An orchestrator is deleted only once every agent of it is deleted, and the evidence and transcripts its agents kept
    /// go with it only when asked.
    /// </summary>
    [Fact]
    public async Task AnOrchestrator_IsDeletedOnlyOnceItsAgentsAre_AndItsKeptEvidenceOnlyWhenAsked()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");

        var live = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, "o1", deleteEvidence: false, Token);

        Assert.Equal(HarnessExit.Refused, live.ExitCode);
        Assert.Contains("its agent(s) 'ag' are not deleted yet", live.Message);

        Assert.True((await kit.DeleteAsync("ag", apply: true, discard: true)).Succeeded);

        var kept = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, "o1", deleteEvidence: false, Token);

        Assert.Equal(HarnessExit.Refused, kept.ExitCode);
        Assert.Contains("because it keeps 1 evidence file(s) and 0 transcript file(s) of its agents 'ag'", kept.Message);

        var deleted = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, "o1", deleteEvidence: true, Token);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.False(Directory.Exists(kit.Layout.Directory));
        Assert.False(Directory.Exists(kit.Worktrees));
    }

    /// <summary>
    /// The mutation workers kept beside an agent's worktree are no worktrees below its orchestrator, and nothing a
    /// removal left: they go with their agent, and one left where its agent is gone - by a build that did not remove
    /// them - goes with the orchestrator, whose deletion it never refuses.
    /// </summary>
    [Fact]
    public async Task AnAgentsMutationWorkers_GoWithIt_AndOneLeftBehindGoesWithItsOrchestrator()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var worker = await WorkerAsync(kit, worktree + ".mutation-357e24cw-1");

        var listed = await kit.Harness.WorktreeService.ListAsync(kit.Main, Token);

        Assert.Equal(["o1/ag"], listed.Select(listing => listing.Name));
        Assert.DoesNotContain("holds a .git entry", kit.Harness.StandardError.ToString(), StringComparison.Ordinal);

        // An orchestrator whose agent is not deleted yet is refused, and changes nothing: its agent's workers stay with it.
        var refused = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, "o1", deleteEvidence: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.True(Directory.Exists(worktree));
        Assert.True(Directory.Exists(worker));

        Assert.True((await kit.DeleteAsync("ag", apply: true, discard: true)).Succeeded);
        Assert.False(Directory.Exists(worker));

        var left = await WorkerAsync(kit, kit.Worktree("old") + ".mutation-357e24cw-1");
        var deleted = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, "o1", deleteEvidence: true, Token);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.False(Directory.Exists(left));
        Assert.False(Directory.Exists(kit.Worktrees));
        Assert.Contains(
            deleted.Details ?? [],
            line => line.StartsWith("its agent 'old': removed 1 mutation worker(s) kept beside it, ", StringComparison.Ordinal)
                && line.EndsWith($": '{left}'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A worker left beside an agent that is gone is said by its agent's address, with the command that removes it; and
    /// one a sweep still running holds never keeps its orchestrator from being deleted - it is left, and said, with what
    /// removes it once the sweep has ended.
    /// </summary>
    [Fact]
    public async Task AWorkerLeftBesideAnAgentThatIsGone_IsSaidByItsAddress_AndOneASweepHoldsIsLeftAndSaid()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var left = await WorkerAsync(kit, kit.Worktree("old") + ".mutation-357e24cw-1");
        var copies = new RepoHarness.Core.Mutations.WorkerCopies(
            SyncKit.Service(kit.Harness),
            kit.Harness.LocalTransport,
            kit.Harness.FileSystem,
            kit.Harness.Output,
            kit.Harness.Identity,
            RepoHarness.Core.Mutations.MutationService.CommandName);
        var sweep = RepoHarness.Core.Execution.RunId.New();

        Assert.Empty(await kit.Harness.WorktreeService.ListAsync(kit.Main, Token));
        Assert.Contains(
            "list-worktree: WARN - 'o1/old.mutation-357e24cw-1' is a mutation worker of the worktree 'o1/old', which is gone: "
            + "'dssharness delete-worktree o1/old' removes it.",
            kit.Harness.StandardError.ToString(),
            StringComparison.Ordinal);

        Assert.True(copies.Claim(left, sweep, force: false).Taken);

        var deleted = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, "o1", deleteEvidence: true, Token);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.True(Directory.Exists(left));
        Assert.Contains(
            "its agent 'old': Worktree 'o1/old' is gone already, and a mutation worker left beside it is not yet removed: "
            + "run 'dssharness delete-worktree o1/old' once what keeps it is gone.",
            deleted.Details ?? []);
        Assert.Contains(
            deleted.Details ?? [],
            line => line.StartsWith($"left the mutation worker '{left}' kept beside it: a sweep still running holds it: ", StringComparison.Ordinal));

        copies.Release(left, sweep);

        var gone = await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "o1/old", force: false, deleteEvidence: false, cancellationToken: Token);

        Assert.True(gone.Succeeded, gone.Outcome.Message);
        Assert.False(Directory.Exists(left));
    }

    /// <summary>
    /// A directory under the orchestrator's that is nobody's worktree is never deleted for a worker named beside it:
    /// asked as deleting a worktree asks, unforced, it is refused and said, and it stays with what it holds.
    /// </summary>
    [Fact]
    public async Task ADirectoryThatIsNoWorktree_IsNeverDeletedForAWorkerNamedBesideIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var notes = kit.Worktree("notes");

        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "notes.txt"), "mine");
        await WorkerAsync(kit, notes + ".mutation-357e24cw-1");

        var deleted = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, "o1", deleteEvidence: true, Token);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(notes, "notes.txt")));
        Assert.Contains(deleted.Details ?? [], line => line.StartsWith("its agent 'notes': ", StringComparison.Ordinal));
    }

    /// <summary>A mutation worker as a sync makes one: marked as the harness's, a repository of its own, holding a file.</summary>
    private static async Task<string> WorkerAsync(OrchestrationKit kit, string path)
    {
        await kit.Harness.LocalTransport.CreateRootAsync(path, RepoHarness.Core.Sync.CopyMark.Complete, Token);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        File.WriteAllText(Path.Combine(path, "main.c"), "int main;");

        return path;
    }

    /// <summary>list-orchestrator's JSON names each orchestrator and each agent with where it stands and where its records are.</summary>
    [Fact]
    public async Task TheListing_NamesEachAgentAndWhereItStands()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag", session: "sess-1");

        var listed = await kit.Harness.OrchestratorService.ListAsync(kit.Main, name: null, json: true, Token);

        Assert.True(listed.Succeeded, OrchestrationKit.Describe(listed));

        var document = JsonNode.Parse(Assert.Single(listed.Data))!;
        var orchestrator = Assert.Single(document["orchestrators"]!.AsArray())!;
        var agent = Assert.Single(orchestrator["agents"]!.AsArray())!;

        Assert.Equal("o1", (string?)orchestrator["name"]);
        Assert.Equal(4, (int?)orchestrator["parallel"]);
        Assert.Equal("ag", (string?)agent["name"]);
        Assert.Equal(AgentStates.Live, (string?)agent["state"]);
        Assert.Equal("o1/ag", (string?)agent["worktree"]);
        Assert.True((bool?)agent["worktreeExists"]);
        Assert.Equal("sess-1", (string?)agent["session"]);
        Assert.Equal(kit.Layout.AgentRecordFile("ag"), (string?)agent["record"]);
    }

    /// <summary>
    /// An orchestrator whose removal stops part way keeps its record, removed last, so it is still there to delete again
    /// rather than leftovers nothing names; run again, the deletion finishes.
    /// </summary>
    [Fact]
    public async Task AnOrchestratorWhoseRemovalStopsPartWay_KeepsItsRecord_AndIsDeletedWhenRunAgain()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var stuck = new StuckDirectoryFileSystem(kit.Harness.FileSystem, kit.Layout.LogsDirectory);
        var service = new OrchestratorService(kit.Harness.ContextLoader, kit.Harness.GitClient, stuck, kit.Harness.Platform, kit.Harness.Output, kit.Harness.WorktreeService, kit.Harness.OrchestrationStore, kit.Harness.OrchestrationLog, TimeProvider.System);

        var stopped = await service.DeleteAsync(kit.Main, "o1", deleteEvidence: false, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.True(File.Exists(kit.Layout.RecordFile));

        var deleted = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, "o1", deleteEvidence: false, Token);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.False(Directory.Exists(kit.Layout.Directory));
    }

    /// <summary>A list of worktrees git cannot give is never read as no worktree below the orchestrator's directory.</summary>
    [Fact]
    public async Task DeletingAnOrchestrator_WhenGitCannotListItsWorktrees_IsNeverReadAsNoneBelow()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        Directory.CreateDirectory(kit.Worktrees);
        var git = new InterceptingGitClient(kit.Harness.GitClient) { ListWorktreesFailure = new HarnessException(HarnessExit.CommandFailed, "Could not list the worktrees: broken") };
        var service = new OrchestratorService(kit.Harness.ContextLoader, git, kit.Harness.FileSystem, kit.Harness.Platform, kit.Harness.Output, kit.Harness.WorktreeService, kit.Harness.OrchestrationStore, kit.Harness.OrchestrationLog, TimeProvider.System);

        var failure = await Assert.ThrowsAsync<HarnessException>(() => service.DeleteAsync(kit.Main, "o1", deleteEvidence: false, Token));

        Assert.Equal(HarnessExit.CommandFailed, failure.ExitCode);
        Assert.True(File.Exists(kit.Layout.RecordFile));
    }

    /// <summary>The real file system, except that one directory cannot be deleted, as while a program holds a file in it.</summary>
    private sealed class StuckDirectoryFileSystem(Core.FileSystem.IFileSystem inner, string stuck) : PassThroughFileSystem(inner)
    {
        public override void DeleteDirectory(string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(stuck), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
            }

            base.DeleteDirectory(path);
        }
    }

    /// <summary>
    /// A removal that stops inside an agent's directory leaves that agent's record, removed after everything else of it,
    /// so the orchestrator's deletion finishes when run again rather than being refused over a directory nothing names.
    /// </summary>
    [Fact]
    public async Task ARemovalStoppedInsideAnAgent_KeepsThatAgentsRecord_AndFinishesWhenRunAgain()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        Assert.True((await kit.DeleteAsync("ag", apply: true, discard: true)).Succeeded);
        OrchestrationKit.Write(kit.Layout.AgentDirectory("ag"), "notes/handoff.md", "done\n");
        var stuck = new StuckDirectoryFileSystem(kit.Harness.FileSystem, Path.Combine(kit.Layout.AgentDirectory("ag"), "notes"));
        var service = new OrchestratorService(kit.Harness.ContextLoader, kit.Harness.GitClient, stuck, kit.Harness.Platform, kit.Harness.Output, kit.Harness.WorktreeService, kit.Harness.OrchestrationStore, kit.Harness.OrchestrationLog, TimeProvider.System);

        var stopped = await service.DeleteAsync(kit.Main, "o1", deleteEvidence: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.True(File.Exists(kit.Layout.AgentRecordFile("ag")));

        var deleted = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, "o1", deleteEvidence: true, Token);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.False(Directory.Exists(kit.Layout.Directory));
    }

    /// <summary>A directory under the worktrees root that cannot be removed once the orchestrator is gone is said beside the deletion, never in place of it.</summary>
    [Fact]
    public async Task ADirectoryUnderTheRootThatCannotBeRemoved_IsSaid_AndTheOrchestratorIsStillDeleted()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        Directory.CreateDirectory(kit.Worktrees);
        var stuck = new StuckDirectoryFileSystem(kit.Harness.FileSystem, kit.Worktrees);
        var service = new OrchestratorService(kit.Harness.ContextLoader, kit.Harness.GitClient, stuck, kit.Harness.Platform, kit.Harness.Output, kit.Harness.WorktreeService, kit.Harness.OrchestrationStore, kit.Harness.OrchestrationLog, TimeProvider.System);

        var deleted = await service.DeleteAsync(kit.Main, "o1", deleteEvidence: false, Token);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Contains(deleted.Details!, line => line.StartsWith($"its directory under the worktrees root, '{kit.Worktrees}', is empty and could not be removed", StringComparison.Ordinal));
        Assert.False(Directory.Exists(kit.Layout.Directory));
    }

    /// <summary>
    /// list-orchestrator's text names where each agent stands - live, deleted and abandoned, or unreadable - and counts its
    /// open agents as create-agent does: every one not deleted, one whose record does not read among them.
    /// </summary>
    [Fact]
    public async Task TheListing_SaysWhereEachAgentStands_AndCountsItsOpenAgents()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("live");
        await kit.CreateAgentAsync("gone");
        Assert.True((await kit.DeleteAsync("gone", apply: true, discard: true)).Succeeded);
        OrchestrationKit.Write(kit.Layout.AgentDirectory("odd"), "agent.json", "{");

        var listed = await kit.Harness.OrchestratorService.ListAsync(kit.Main, name: null, json: false, Token);

        Assert.True(listed.Succeeded, OrchestrationKit.Describe(listed));
        Assert.Contains(listed.Details!, line => line.StartsWith("o1  model model-a, 2 of at most 4 agent(s) open", StringComparison.Ordinal));
        Assert.Contains(listed.Details!, line => line.StartsWith("  live  live at ", StringComparison.Ordinal));
        Assert.Contains(listed.Details!, line => line.StartsWith("  gone  deleted at ", StringComparison.Ordinal) && line.Contains(", abandoned", StringComparison.Ordinal));
        Assert.Contains(listed.Details!, line => line.StartsWith("  odd  unreadable: ", StringComparison.Ordinal));
    }

    /// <summary>list-orchestrator names one orchestrator when asked, refuses one that does not exist, and says a live agent's worktree is gone where it is.</summary>
    [Fact]
    public async Task TheListingOfOneOrchestrator_SaysAGoneWorktreeIsGone_AndRefusesAnUnknownOne()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        Assert.True((await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "o1/ag", force: true, deleteEvidence: true, cancellationToken: Token)).Succeeded);

        var listed = await kit.Harness.OrchestratorService.ListAsync(kit.Main, "o1", json: true, Token);
        var unknown = await kit.Harness.OrchestratorService.ListAsync(kit.Main, "nope", json: true, Token);

        var agent = Assert.Single(Assert.Single(JsonNode.Parse(Assert.Single(listed.Data))!["orchestrators"]!.AsArray())!["agents"]!.AsArray())!;
        Assert.False((bool?)agent["worktreeExists"]);
        Assert.Equal(worktree, (string?)agent["path"]);
        Assert.Equal(HarnessExit.Refused, unknown.ExitCode);
        Assert.StartsWith("No orchestrator named 'nope'.", unknown.Message);
    }

    /// <summary>An orchestrator allowing no agent at all is a usage error.</summary>
    [Fact]
    public async Task AnOrchestratorAllowingNoAgent_IsAUsageError()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);

        var refused = await kit.Harness.OrchestratorService.CreateAsync(kit.Main, "o2", "model-a", 0, null, Token);

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.False(Directory.Exists(OrchestratorLayout.Of(new Core.Repository.HarnessLayout(kit.Main, kit.Main), "o2").Directory));
    }
}
