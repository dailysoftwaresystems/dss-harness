using System.Text.Json.Nodes;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

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
    /// The mutation workers kept in an agent's worktree are no worktrees below its orchestrator, and nothing a removal
    /// left: the listing says nothing of them, deleting the agent removes them before its worktree, and the orchestrator
    /// then goes with nothing left below it.
    /// </summary>
    [Fact]
    public async Task AnAgentsMutationWorkers_AreKeptInItsWorktree_AndGoWithIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var worker = await WorkerAsync(kit, HarnessLayout.InSlot(worktree, HarnessLayout.MutationsDirectoryName, "357e24cw-1"));

        var listed = await kit.Harness.WorktreeService.ListAsync(kit.Main, Token);

        Assert.Equal(["o1/ag"], listed.Select(listing => listing.Name));
        Assert.DoesNotContain("holds a .git entry", kit.Harness.StandardError.ToString(), StringComparison.Ordinal);

        // An orchestrator whose agent is not deleted yet is refused, and changes nothing: its agent's workers stay with it.
        var refused = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, "o1", deleteEvidence: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.True(Directory.Exists(worker));

        var agent = await kit.DeleteAsync("ag", apply: true, discard: true);

        Assert.True(agent.Succeeded, OrchestrationKit.Describe(agent));
        Assert.Contains(
            agent.Details ?? [],
            line => line.Contains("removed 1 mutation worker(s) kept in it, ", StringComparison.Ordinal) && line.EndsWith($": '{worker}'", StringComparison.Ordinal));
        Assert.False(Directory.Exists(worktree));

        var deleted = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, "o1", deleteEvidence: true, Token);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.False(Directory.Exists(kit.Worktrees));
    }

    /// <summary>
    /// A worker an earlier version of this tool kept beside an agent's worktree is known to nothing here: the listing
    /// says it as any directory holding a .git entry git records no worktree at, deleting its agent leaves it, and its
    /// orchestrator is not deleted while it is there - deleting it would delete what nothing here can tell - until a
    /// person who has looked in it deletes it by hand.
    /// </summary>
    [Fact]
    public async Task AWorkerAnEarlierVersionKeptBesideAnAgent_IsAPersonsToDelete_AndHoldsItsOrchestratorUntilThen()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var old = await WorkerAsync(kit, worktree + ".mutation-357e24cw-1");

        var listed = await kit.Harness.WorktreeService.ListAsync(kit.Main, Token);

        Assert.Equal(["o1/ag"], listed.Select(listing => listing.Name));
        Assert.Contains(
            $"list-worktree: WARN - 'o1/ag.mutation-357e24cw-1' holds a .git entry, and git records no worktree there: {WorktreeInspector.UnrecordedWhy}. "
            + $"It is not listed, and no worktree can be made at its path until it is gone; look inside for anything wanted, then delete '{old}'.",
            kit.Harness.StandardError.ToString(),
            StringComparison.Ordinal);

        var agent = await kit.DeleteAsync("ag", apply: true, discard: true);

        Assert.True(agent.Succeeded, OrchestrationKit.Describe(agent));
        Assert.DoesNotContain(agent.Details ?? [], line => line.Contains("mutation worker", StringComparison.Ordinal));
        Assert.True(Directory.Exists(old));

        var refused = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, "o1", deleteEvidence: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"Orchestrator 'o1' was not deleted: git records no worktree at '{old}', though it holds a .git entry of its own - {WorktreeInspector.UnrecordedWhy}. "
            + "Look inside it for anything wanted, then delete it. Nothing was changed.",
            refused.Message);
        Assert.True(Directory.Exists(old));
        Assert.True(File.Exists(kit.Layout.RecordFile));

        Directory.Delete(old, recursive: true);

        var deleted = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, "o1", deleteEvidence: true, Token);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.False(Directory.Exists(kit.Worktrees));
    }

    /// <summary>
    /// An orchestrator's directory under the worktrees root that cannot be looked in is never read as holding nothing:
    /// the orchestrator is not deleted, and nothing is changed.
    /// </summary>
    [Fact]
    public async Task DeletingAnOrchestrator_WhoseDirectoryUnderTheRootCannotBeLookedIn_ChangesNothing()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        Directory.CreateDirectory(kit.Worktrees);
        var service = Over(kit, new UnlistableFileSystem(kit.Harness.FileSystem, kit.Worktrees));

        var failure = await Assert.ThrowsAsync<HarnessException>(() => service.DeleteAsync(kit.Main, "o1", deleteEvidence: false, Token));

        Assert.Equal(HarnessExit.CommandFailed, failure.ExitCode);
        Assert.StartsWith($"'{kit.Worktrees}' could not be looked in: Access to the path '", failure.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(kit.Layout.RecordFile));
        Assert.True(Directory.Exists(kit.Worktrees));
    }

    /// <summary>
    /// A directory under the worktrees root that cannot be looked in once the orchestrator is gone is said as that -
    /// never as empty, which nothing told - and is left where it is, beside the deletion.
    /// </summary>
    [Fact]
    public async Task ADirectoryUnderTheRootThatCannotBeLookedIn_IsSaidAsThat_AndTheOrchestratorIsStillDeleted()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        Directory.CreateDirectory(kit.Worktrees);
        var service = Over(kit, new CannotTellEmpty(kit.Harness.FileSystem, kit.Worktrees));

        var deleted = await service.DeleteAsync(kit.Main, "o1", deleteEvidence: false, Token);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Contains(
            $"its directory under the worktrees root, '{kit.Worktrees}', is left: whether it is empty could not be told - the disk would not say",
            deleted.Details ?? []);
        Assert.True(Directory.Exists(kit.Worktrees));
        Assert.False(Directory.Exists(kit.Layout.Directory));
    }

    /// <summary>A disk that will not say which files one directory holds, so not whether it is empty either.</summary>
    private sealed class CannotTellEmpty(RepoHarness.Core.FileSystem.IFileSystem inner, string directory) : PassThroughFileSystem(inner)
    {
        public override IEnumerable<string> EnumerateFiles(string path, bool recursive)
            => string.Equals(Path.GetFullPath(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)
                ? throw new IOException("the disk would not say.")
                : base.EnumerateFiles(path, recursive);
    }

    /// <summary>Deleting orchestrators over <paramref name="fileSystem"/>, the worktrees below them deleted over it as well.</summary>
    private static OrchestratorService Over(OrchestrationKit kit, RepoHarness.Core.FileSystem.IFileSystem fileSystem)
        => new(
            kit.Harness.ContextLoader,
            kit.Harness.GitClient,
            fileSystem,
            kit.Harness.Platform,
            kit.Harness.Output,
            new WorktreeService(
                kit.Harness.ContextLoader,
                kit.Harness.GitClient,
                fileSystem,
                kit.Harness.PathBudget,
                kit.Harness.Platform,
                kit.Harness.Output,
                kit.Harness.HostCopies,
                kit.Harness.Local(fileSystem),
                kit.Harness.RunLock),
            kit.Harness.OrchestrationStore,
            kit.Harness.OrchestrationLog,
            TimeProvider.System);

    /// <summary>A mutation worker as a sync makes one: marked as the harness's, a repository of its own, holding a file.</summary>
    private static async Task<string> WorkerAsync(OrchestrationKit kit, string path)
    {
        await kit.Harness.LocalTransport.CreateRootAsync(path, RepoHarness.Core.Sync.CopyMark.Complete, Token);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        File.WriteAllText(Path.Combine(path, "main.c"), "int main;");

        return path;
    }

    /// <summary>
    /// list-orchestrator's JSON names each orchestrator and each agent with where it stands and where its records are, the
    /// directory the bytes of what it shares are kept in among them.
    /// </summary>
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
        Assert.Equal(kit.Layout.SeedFile("ag"), (string?)agent["seed"]);
        Assert.Equal(kit.Layout.SharedDirectory("ag"), (string?)agent["shared"]);
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
