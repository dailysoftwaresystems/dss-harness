using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>create-agent, seed-agent and refresh-agent: an agent's record, worktree and seed, and what it is handed later.</summary>
public sealed class AgentServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// An agent is made with its record, its worktree below its orchestrator's directory, its work and plans directories,
    /// and a seed holding the main tree's uncommitted state, each path with its digest; the base is the commit it was made from.
    /// </summary>
    [Fact]
    public async Task AnAgent_IsMadeWithItsWorktreeRecordAndSeed()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        OrchestrationKit.Write(kit.Main, "untracked.txt", "u\n");

        var worktree = await kit.CreateAgentAsync("ag", session: "abc123");
        var record = kit.Record("ag");
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.Equal(AgentStates.Live, record.State);
        Assert.Equal("o1/ag", record.Worktree);
        Assert.Equal("model-b", record.Model);
        Assert.Equal("abc123", record.Session);
        Assert.Equal(await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token), record.Base);
        Assert.Equal(["a.txt", "untracked.txt"], seed.Paths.Keys.Order(StringComparer.Ordinal));
        Assert.False(seed.Empty);
        Assert.Equal("one\nmain edit\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.True(Directory.Exists(kit.Layout.WorkDirectory("ag")));
        Assert.True(Directory.Exists(kit.Layout.PlansDirectory("ag")));
        Assert.True(Directory.Exists(kit.Layout.RowsDirectory("ag")));
        Assert.Contains(kit.Harness.OrchestrationLog.Read(kit.Layout.LogFile("ag")), entry => entry.Command == AgentService.CreateCommand && entry.Outcome == nameof(HarnessExit.Success));
    }

    /// <summary>--empty hands the agent nothing and records that it was handed nothing.</summary>
    [Fact]
    public async Task AnEmptySeed_HandsNothing_AndSaysSo()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");

        var worktree = await kit.CreateAgentAsync("ag", empty: true);
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.True(seed.Empty);
        Assert.Empty(seed.Paths);
        Assert.Equal("one\n", OrchestrationKit.Read(worktree, "a.txt"));
    }

    /// <summary>
    /// The orchestrator's limit is the most agents with a worktree at once: the next is refused, naming those that have
    /// one, and a deleted agent frees its place.
    /// </summary>
    [Fact]
    public async Task TheOrchestratorsLimit_RefusesTheNextAgent_UntilOneIsDeleted()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp, parallel: 1);
        await kit.CreateAgentAsync("first");

        var refused = await kit.Harness.AgentService.CreateAsync(kit.Main, OrchestrationKit.Orchestrator, "second", "model-b", false, null, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith("Orchestrator 'o1' already has 1 open agent(s), its limit: first.", refused.Message);
        Assert.False(Directory.Exists(kit.Layout.AgentDirectory("second")));

        Assert.True((await kit.DeleteAsync("first", apply: true, discard: true)).Succeeded);
        await kit.CreateAgentAsync("second");
    }

    /// <summary>
    /// Run again, create-agent records the session and nothing else; another model is refused, and a deleted agent's name
    /// is never used again.
    /// </summary>
    [Fact]
    public async Task RunAgain_CreateAgentRecordsOnlyTheSession()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");

        var session = await kit.Harness.AgentService.CreateAsync(kit.Main, OrchestrationKit.Orchestrator, "ag", "model-b", false, "sess-1", Token);
        var model = await kit.Harness.AgentService.CreateAsync(kit.Main, OrchestrationKit.Orchestrator, "ag", "model-c", false, null, Token);

        Assert.True(session.Succeeded, OrchestrationKit.Describe(session));
        Assert.Equal("sess-1", kit.Record("ag").Session);
        Assert.Equal(HarnessExit.Refused, model.ExitCode);

        Assert.True((await kit.DeleteAsync("ag", apply: true, discard: true)).Succeeded);

        var reused = await kit.Harness.AgentService.CreateAsync(kit.Main, OrchestrationKit.Orchestrator, "ag", "model-b", false, null, Token);

        Assert.Equal(HarnessExit.Refused, reused.ExitCode);
        Assert.Contains("an agent's name is never used twice", reused.Message);
    }

    /// <summary>An agent is never named as its orchestrator, nor made for an orchestrator that does not exist.</summary>
    [Fact]
    public async Task AnAgentNamedAsItsOrchestrator_OrOfNoOrchestrator_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);

        var named = await kit.Harness.AgentService.CreateAsync(kit.Main, "o1", "o1", "model-b", false, null, Token);
        var none = await Assert.ThrowsAsync<HarnessException>(() => kit.Harness.AgentService.CreateAsync(kit.Main, "o2", "ag", "model-b", false, null, Token));

        Assert.Equal(HarnessExit.UsageError, named.ExitCode);
        Assert.Equal(HarnessExit.Refused, none.ExitCode);
        Assert.StartsWith("No orchestrator named 'o2'.", none.Message);
    }

    /// <summary>A worktree that cannot be made makes no agent: its place is given back, and its directory is gone.</summary>
    [Fact]
    public async Task AnAgentWhoseWorktreeCannotBeMade_LeavesNothingBehind()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Worktree("ag"), "squatter.txt", "here first\n");

        var refused = await kit.Harness.AgentService.CreateAsync(kit.Main, OrchestrationKit.Orchestrator, "ag", "model-b", false, null, Token);

        Assert.False(refused.Succeeded);
        Assert.EndsWith("No agent was created.", refused.Message);
        Assert.False(Directory.Exists(kit.Layout.AgentDirectory("ag")));
    }

    /// <summary>
    /// seed-agent refuses an agent holding changes of its own, whose files the copies would overwrite, unless forced; an
    /// empty seed copies nothing and needs no force.
    /// </summary>
    [Fact]
    public async Task SeedingAgain_RefusesAnAgentWithChangesOfItsOwn_UnlessForced()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");

        var refused = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: false, Token);
        var empty = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: true, force: false, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("already holds 1 changed path(s) of its own - b.txt -", refused.Message);
        Assert.True(empty.Succeeded, OrchestrationKit.Describe(empty));
        Assert.True(kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Empty);

        var forced = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: true, Token);

        Assert.True(forced.Succeeded, OrchestrationKit.Describe(forced));
        Assert.Equal("one\nmain edit\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal(["a.txt"], kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths.Keys);
    }

    /// <summary>
    /// refresh-agent copies the main tree's changed files under a path into a live agent - a dry run until --apply - and
    /// records them as handed to it, so its fold leaves them out; one the agent edited is refused.
    /// </summary>
    [Fact]
    public async Task Refreshing_CopiesTheMainTreesChanges_AndTheFoldLeavesThemOut()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nregistry row\n");
        OrchestrationKit.Write(kit.Main, "docs/new.md", "new\n");

        var dry = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", ["docs"], apply: false, Token);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Equal(["  docs/new.md", "  docs/x.md"], dry.Details);
        Assert.Equal("x\n", OrchestrationKit.Read(worktree, "docs/x.md"));

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", ["docs"], apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("x\nregistry row\n", OrchestrationKit.Read(worktree, "docs/x.md"));

        var fold = await kit.FoldAsync("ag", apply: false);

        Assert.True(fold.Succeeded, OrchestrationKit.Describe(fold));
        Assert.Contains("2 inherited path(s) left out; 0 path(s) are its own:", fold.Details!);

        OrchestrationKit.Write(worktree, "docs/x.md", "x\nagent's own\n");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nanother row\n");

        var refused = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", ["docs"], apply: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("changed 1 of the path(s) to refresh - docs/x.md -", refused.Message);
        Assert.Equal("x\nagent's own\n", OrchestrationKit.Read(worktree, "docs/x.md"));
    }

    /// <summary>With no path named, refresh-agent refreshes the directory the anchor registries are kept in.</summary>
    [Fact]
    public async Task Refreshing_WithNoPath_RefreshesTheRegistriesDirectory()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var written = await kit.Harness.AnchorRegistryService.WriteAsync(
            kit.Main,
            new Core.Anchors.AnchorWriteRequest("D-TEST-REFRESH-ROW", "P2", "a row applied while the agent works"),
            dryRun: false,
            Token);
        Assert.True(written.Written);

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", [], apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains("D-TEST-REFRESH-ROW", OrchestrationKit.Read(worktree, written.To.RelativePath));
    }

    /// <summary>refresh-agent never puts back a file the agent deleted: copying the main tree's over it would undo the deletion unseen.</summary>
    [Fact]
    public async Task Refreshing_NeverUndoesAnAgentsDeletion()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        File.Delete(Path.Combine(worktree, "docs", "x.md"));
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nregistry row\n");

        var refused = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", ["docs"], apply: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("changed 1 of the path(s) to refresh - docs/x.md -", refused.Message);
        Assert.False(File.Exists(Path.Combine(worktree, "docs", "x.md")));
    }

    /// <summary>A symbolic link among what an agent would be handed is refused before its worktree is made: nothing is left behind.</summary>
    [Fact]
    public async Task ALinkAmongWhatAnAgentWouldBeHanded_IsRefusedBeforeAnythingIsMade()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        TestLinks.OrSkip(() => File.CreateSymbolicLink(Path.Combine(kit.Main, "linked.txt"), Path.Combine(kit.Main, "a.txt")));

        var refused = await Assert.ThrowsAsync<HarnessException>(() => kit.Harness.AgentService.CreateAsync(kit.Main, "o1", "ag", "model-b", false, null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("'linked.txt' is a symbolic link", refused.Message);
        Assert.False(Directory.Exists(kit.Layout.AgentDirectory("ag")));
        Assert.False(Directory.Exists(kit.Worktree("ag")));
    }

    /// <summary>Run again for an agent whose making did not finish, create-agent says so and names how to finish, never "already as asked".</summary>
    [Fact]
    public async Task AnAgentWhoseMakingDidNotFinish_IsSaidToBeOne()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        File.Delete(kit.Layout.SeedFile("ag"));

        var again = await kit.Harness.AgentService.CreateAsync(kit.Main, "o1", "ag", "model-b", false, null, Token);

        Assert.Equal(HarnessExit.Refused, again.ExitCode);
        Assert.Contains("making it did not finish: it was never seeded", again.Message);
    }

    /// <summary>
    /// An agent whose worktree was made under another worktrees root than the one the configuration names now is refused,
    /// and nothing of it is touched: delete-worktree would look for it where it is not.
    /// </summary>
    [Fact]
    public async Task AnAgentMadeUnderAnotherWorktreesRoot_IsRefused_AndNothingIsTouched()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        kit.Harness.WriteConfig(kit.Main, new Core.Configuration.HarnessConfig { Worktrees = OrchestrationKit.SettingsWith(root: ".elsewhere") });

        var refused = await kit.DeleteAsync("ag", apply: true, discard: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("was made under worktrees root '.worktrees', and the configuration names '.elsewhere' now", refused.Message);
        Assert.True(Directory.Exists(worktree));
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
    }

    /// <summary>
    /// A hand-over that stops part way records exactly what was handed and names its records, so a fold never takes a
    /// copied file for the agent's own work.
    /// </summary>
    [Fact]
    public async Task AHandOverThatStopsPartWay_RecordsOnlyWhatWasHanded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmain edit\n");
        var agents = kit.Harness.Agents(new UncopyableFileSystem(kit.Harness.FileSystem, Path.Combine(kit.Main, "b.txt")), kit.Harness.AnchorRegistryService);

        var stopped = await agents.CreateAsync(kit.Main, "o1", "ag", "model-b", false, null, Token);
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Contains("stopped after 1 of 2 path(s)", stopped.Message);
        Assert.Contains($"seed {kit.Layout.SeedFile("ag")}", stopped.Details!);
        Assert.Equal(["a.txt"], seed.Paths.Keys);
    }

    /// <summary>Seeding again is never refused over a copy the agent was handed and left alone: that is not a change of its own.</summary>
    [Fact]
    public async Task SeedingAgain_NeverTakesACopyItWasHandedForItsOwnChange()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        await kit.CreateAgentAsync("ag");

        var seeded = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: false, Token);

        Assert.True(seeded.Succeeded, OrchestrationKit.Describe(seeded));
    }

    /// <summary>Seeding again with nothing keeps what the agent was handed before: a copy still in its worktree is never taken for its own work.</summary>
    [Fact]
    public async Task SeedingAgainWithNothing_KeepsWhatItWasHandedBefore()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        await kit.CreateAgentAsync("ag");

        var seeded = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: true, force: false, Token);

        Assert.True(seeded.Succeeded, OrchestrationKit.Describe(seeded));
        Assert.Contains("what it was handed before stays recorded", seeded.Message);
        Assert.Contains("a.txt", kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths.Keys);
    }

    /// <summary>Refreshing is refused over a file the agent changed that it was never handed: the main tree's copy would undo its work.</summary>
    [Fact]
    public async Task Refreshing_IsRefusedOverAFileTheAgentChanged_ThatItWasNeverHanded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmain edit\n");

        var refused = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", ["b.txt"], apply: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("changed 1 of the path(s) to refresh - b.txt -", refused.Message);
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(worktree, "b.txt"));
    }

    /// <summary>Refreshing is refused over a new file both trees made: the agent's own would be replaced by the main tree's.</summary>
    [Fact]
    public async Task Refreshing_IsRefusedOverANewFileBothTreesMade()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "n.txt", "the agent's\n");
        OrchestrationKit.Write(kit.Main, "n.txt", "the main tree's\n");

        var refused = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", ["n.txt"], apply: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("changed 1 of the path(s) to refresh - n.txt -", refused.Message);
        Assert.Equal("the agent's\n", OrchestrationKit.Read(worktree, "n.txt"));
    }

    /// <summary>Two agents made at once never both take an orchestrator's last place: its limit is counted and taken in one step.</summary>
    [Fact]
    public async Task TwoAgentsMadeAtOnce_NeverBothTakeTheLastPlace()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp, parallel: 1);

        var made = await Task.WhenAll(
            Task.Run(() => kit.Harness.AgentService.CreateAsync(kit.Main, "o1", "a1", "model-b", false, null, Token), Token),
            Task.Run(() => kit.Harness.AgentService.CreateAsync(kit.Main, "o1", "a2", "model-b", false, null, Token), Token));

        Assert.Single(made, outcome => outcome.Succeeded);
        Assert.Single(made, outcome => outcome.ExitCode == HarnessExit.Refused && outcome.Message.Contains("open agent(s), its limit", StringComparison.Ordinal));
    }

    /// <summary>A session, a model or a name that is not one is a usage error before anything is read or written: a line break at its end among them.</summary>
    [Theory]
    [InlineData("ag", "../../x")]
    [InlineData("ag", "abc\n")]
    [InlineData("ag\n", null)]
    public async Task ASessionOrANameThatIsNotOne_IsAUsageError(string agent, string? session)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);

        var refused = await kit.Harness.AgentService.CreateAsync(kit.Main, "o1", agent, "model-b", false, session, Token);

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.False(Directory.Exists(kit.Layout.AgentDirectory("ag")));
    }

    /// <summary>A path given on the command line that is not relative to the tree - rooted, or climbing out of it - is a usage error.</summary>
    [Fact]
    public async Task APathGivenThatIsNotRelativeToTheTree_IsAUsageError()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");

        var settled = await kit.FoldAsync("ag", apply: true, "/etc/passwd");
        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", ["../outside"], apply: true, Token);

        Assert.Equal(HarnessExit.UsageError, settled.ExitCode);
        Assert.Equal(HarnessExit.UsageError, refreshed.ExitCode);
    }

    /// <summary>The real file system, except that one file cannot be copied, as while another program holds it.</summary>
    private sealed class UncopyableFileSystem(Core.FileSystem.IFileSystem inner, string held) : PassThroughFileSystem(inner)
    {
        public override void CopyFile(string source, string destination, bool overwrite = false)
        {
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(held), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"The process cannot access the file '{source}' because it is being used by another process.");
            }

            base.CopyFile(source, destination, overwrite);
        }
    }
}
