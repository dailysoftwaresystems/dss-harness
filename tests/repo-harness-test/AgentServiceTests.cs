using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>create-agent, seed-agent and refresh-agent: an agent's record, worktree and seed, and what it is handed later.</summary>
public sealed class AgentServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// An agent is made with its record, its worktree below its orchestrator's directory, its work, plans and rows directories,
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

        var dry = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: false, Token);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Equal(["  docs/new.md", "  docs/x.md"], dry.Details);
        Assert.Equal("x\n", OrchestrationKit.Read(worktree, "docs/x.md"));

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("x\nregistry row\n", OrchestrationKit.Read(worktree, "docs/x.md"));

        var fold = await kit.FoldAsync("ag", apply: false);

        Assert.True(fold.Succeeded, OrchestrationKit.Describe(fold));
        Assert.Contains("2 inherited path(s) left out; 0 path(s) are its own:", fold.Details!);

        OrchestrationKit.Write(worktree, "docs/x.md", "x\nagent's own\n");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nanother row\n");

        var refused = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: true, Token);

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

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under(), apply: true, Token);

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

        var refused = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: true, Token);

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

        var refused = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("b.txt"), apply: true, Token);

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

        var refused = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("n.txt"), apply: true, Token);

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
        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("../outside"), apply: true, Token);

        Assert.Equal(HarnessExit.UsageError, settled.ExitCode);
        Assert.Equal(HarnessExit.UsageError, refreshed.ExitCode);
    }

    /// <summary>
    /// A path an agent was handed that the main tree has since put back as its HEAD holds it is handed again: git status no
    /// longer lists it, and the agent's copy is stale all the same. A consumer's five steps: one agent's fold hands a sibling
    /// a file, a second fold of it puts the file back, and the sibling refreshed then held the first fold's copy, told OK.
    /// </summary>
    [Fact]
    public async Task Refreshing_HandsAgainAPathTheMainTreePutBackAsItsHeadHoldsIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var first = await kit.CreateAgentAsync("first");
        var second = await kit.CreateAgentAsync("second");

        OrchestrationKit.Write(first, "b.txt", "two\nfirst's edit\n");
        Assert.True((await kit.FoldAsync("first", apply: true)).Succeeded);
        Assert.True((await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "second", RefreshRequest.Under("b.txt"), apply: true, Token)).Succeeded);
        Assert.Equal("two\nfirst's edit\n", OrchestrationKit.Read(second, "b.txt"));

        OrchestrationKit.Write(first, "b.txt", "two\n");
        var undone = await kit.FoldAsync("first", apply: true);
        Assert.True(undone.Succeeded, OrchestrationKit.Describe(undone));
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));

        var dry = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "second", RefreshRequest.Under("b.txt"), apply: false, Token);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Contains("  b.txt", dry.Details!);
        Assert.Equal("two\nfirst's edit\n", OrchestrationKit.Read(second, "b.txt"));

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "second", RefreshRequest.Under("b.txt"), apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("two\n", OrchestrationKit.Read(second, "b.txt"));
        Assert.Equal(await DigestAsync(kit.Main, "b.txt"), kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "second")!.Paths["b.txt"]);
        Assert.Contains("1 inherited path(s) left out; 0 path(s) are its own:", (await kit.FoldAsync("second", apply: false)).Details!);
    }

    /// <summary>
    /// What the main tree committed since an agent's base is handed to it like what it holds uncommitted: a commit does not
    /// make the agent's copy any less stale - a path handed before the commit and changed again since, and one never handed.
    /// Its fold then leaves both out, and the refresh says its base is behind and what moves it.
    /// </summary>
    [Fact]
    public async Task Refreshing_HandsWhatTheMainTreeCommittedSinceTheAgentsBase()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nhanded row\n");
        Assert.True((await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: true, Token)).Succeeded);

        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nhanded row\nanother row\n");
        OrchestrationKit.Write(kit.Main, "docs/new.md", "new\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var dry = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: false, Token);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Equal(["  docs/new.md", "  docs/x.md"], dry.Details!.Take(2));
        Assert.Equal(
            $"its base {Core.Output.ReportText.Commit(kit.Record("ag").Base!)} is not the main tree's HEAD "
            + $"{Core.Output.ReportText.Commit((await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!)}: what the main tree committed "
            + "since reaches it as copies it is handed; 'dssharness rebase-agent o1 ag --apply' moves its base there",
            Assert.Single(dry.Details!.Skip(2)));

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("x\nhanded row\nanother row\n", OrchestrationKit.Read(worktree, "docs/x.md"));
        Assert.Equal("new\n", OrchestrationKit.Read(worktree, "docs/new.md"));
        Assert.Contains("2 inherited path(s) left out; 0 path(s) are its own:", (await kit.FoldAsync("ag", apply: false)).Details!);
    }

    /// <summary>A path an agent was handed as a deletion that the main tree has since restored is handed again: its file goes back.</summary>
    [Fact]
    public async Task Refreshing_HandsAgainAPathHandedAsADeletionThatTheMainTreeRestored()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        File.Delete(Path.Combine(kit.Main, "b.txt"));
        var worktree = await kit.CreateAgentAsync("ag");
        Assert.False(File.Exists(Path.Combine(worktree, "b.txt")));

        await kit.GitAsync(kit.Main, "checkout", "--", "b.txt");

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("b.txt"), apply: true, Token);
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("two\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Contains("b.txt", seed.Paths.Keys);
        Assert.DoesNotContain("b.txt", seed.Absent ?? []);
    }

    /// <summary>
    /// A dry run given several paths names every one it would hand: a stale copy the main tree's status no longer lists
    /// among them, which a consumer's dry run left out of its count without a word.
    /// </summary>
    [Fact]
    public async Task ARefreshDryRun_NamesEveryPathGivenItWouldHand()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmain edit\n");
        var worktree = await kit.CreateAgentAsync("ag");
        await kit.GitAsync(kit.Main, "checkout", "--", "b.txt");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");

        var dry = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt", "b.txt"), apply: false, Token);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.StartsWith("dry run: 2 path(s) would be refreshed", dry.Message, StringComparison.Ordinal);
        Assert.Equal(["  a.txt", "  b.txt"], dry.Details);
        Assert.Equal("two\nmain edit\n", OrchestrationKit.Read(worktree, "b.txt"));
    }

    /// <summary>
    /// seed-agent weighs as refresh-agent does: what the main tree committed since the agent's base is handed to it too, so
    /// its base not being the main tree's HEAD leaves it nothing stale.
    /// </summary>
    [Fact]
    public async Task SeedingAgain_HandsWhatTheMainTreeCommittedSinceTheAgentsBase()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var seeded = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: false, Token);

        Assert.True(seeded.Succeeded, OrchestrationKit.Describe(seeded));
        Assert.Equal("seeded agent 'ag' of 'o1' with 1 path(s)", seeded.Message);
        Assert.Equal("two\ncommitted\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Contains("b.txt", kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths.Keys);
    }

    /// <summary>
    /// An agent made after the main tree committed is made from that commit, and handed only what is uncommitted on top of
    /// it: nothing it starts from is stale.
    /// </summary>
    [Fact]
    public async Task AnAgentMadeAfterTheMainTreeCommitted_IsMadeFromThatCommit_AndHandedWhatIsUncommitted()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "before the agent", Token);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nuncommitted\n");

        var worktree = await kit.CreateAgentAsync("ag");

        Assert.Equal(await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token), kit.Record("ag").Base);
        Assert.Equal("two\ncommitted\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal("one\nuncommitted\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal(["a.txt"], kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths.Keys);
    }

    /// <summary>
    /// Forced, seeding again hands the main tree's whole uncommitted state over the agent's own changes, as making it did:
    /// a file the main tree holds uncommitted and has not moved since it was handed among them, though weighing alone would
    /// leave it as the agent changed it.
    /// </summary>
    [Fact]
    public async Task SeedingAgainForced_HandsTheMainTreesUncommittedFile_OverTheAgentsChange_ThoughTheMainTreeLeftItAsHanded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmain edit\n");
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");

        var refused = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: false, Token);
        var forced = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.True(forced.Succeeded, OrchestrationKit.Describe(forced));
        Assert.Equal("two\nmain edit\n", OrchestrationKit.Read(worktree, "b.txt"));
    }

    /// <summary>
    /// A refresh given paths weighs what the main tree committed under them alone: a change it committed elsewhere is
    /// neither handed nor listed.
    /// </summary>
    [Fact]
    public async Task RefreshingUnderAPath_HandsOnlyWhatTheMainTreeCommittedUnderIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "docs/new.md", "new\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\ncommitted elsewhere\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("refreshed 1 path(s) into agent 'ag' of 'o1', recorded as handed to it, so its fold leaves them out", applied.Message);
        Assert.Equal("  docs/new.md", applied.Details![0]);
        Assert.Equal("one\n", OrchestrationKit.Read(worktree, "a.txt"));
    }

    /// <summary>
    /// A refresh given paths hands again only what the agent was handed under them: a path it was handed elsewhere, which
    /// the main tree has moved since, stays as it was handed, for a refresh that names it.
    /// </summary>
    [Fact]
    public async Task RefreshingUnderAPath_HandsAgainOnlyWhatTheAgentWasHandedUnderIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nhanded\n");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nhanded\n");
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmoved since\n");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nmoved since\n");

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains("  docs/x.md", applied.Details!);
        Assert.DoesNotContain("  a.txt", applied.Details!);
        Assert.Equal("x\nmoved since\n", OrchestrationKit.Read(worktree, "docs/x.md"));
        Assert.Equal("one\nhanded\n", OrchestrationKit.Read(worktree, "a.txt"));
    }

    /// <summary>
    /// A refresh asked for every path hands whatever the main tree moved anywhere in the tree, committed or not, off the
    /// paths never handed to an agent - the orchestrators' directory here, forced past its ignore rule - and takes no path
    /// beside it; the whole tree named as a path says to ask for every path instead.
    /// </summary>
    [Fact]
    public async Task RefreshingEveryPath_HandsWhateverTheMainTreeMoved_OffTheFloor_AndTakesNoPath()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "docs/new.md", "new\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\ncommitted\n");
        OrchestrationKit.Write(kit.Main, ".orchestrators/o1/note.md", "the orchestrator's own\n");
        await kit.GitAsync(kit.Main, "add", "--force", ".orchestrators/o1/note.md");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nnot committed\n");

        var both = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true, Paths = ["docs"] }, apply: true, Token);
        var whole = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("."), apply: true, Token);

        Assert.Equal(HarnessExit.UsageError, both.ExitCode);
        Assert.Equal("--all weighs every path the main tree moved, so it takes no path beside it: give the paths, or --all", both.Message);
        Assert.Equal(HarnessExit.UsageError, whole.ExitCode);
        Assert.Equal("'.' names the whole tree, not a path in it: pass --all to weigh every path the main tree moved", whole.Message);
        Assert.Equal("one\n", OrchestrationKit.Read(worktree, "a.txt"));

        var dry = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true }, apply: false, Token);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Equal("dry run: 3 path(s) would be refreshed into agent 'ag' of 'o1'; pass --apply to hand them over", dry.Message);
        Assert.Equal(["  a.txt", "  b.txt", "  docs/new.md"], dry.Details!.Take(3));

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true }, apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("one\ncommitted\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal("two\nnot committed\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal("new\n", OrchestrationKit.Read(worktree, "docs/new.md"));
        Assert.False(File.Exists(Path.Combine(worktree, ".orchestrators", "o1", "note.md")));

        var again = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true }, apply: true, Token);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Equal("agent 'ag' of 'o1' holds the main tree's copy of every changed path", again.Message);
    }

    /// <summary>
    /// A refresh refused over paths the agent changed names every one of them, and the arguments that leave them as they
    /// are; given those, it hands every other path, says which it left, and leaves each as the agent changed it, its own
    /// work for its fold to weigh. Nothing is left out that nobody named: one of two named still refuses, naming both.
    /// </summary>
    [Fact]
    public async Task ARefreshRefusedOverTheAgentsChanges_NamesEachAndHowToLeaveThem_AndHandsTheRestOnceTheyAreNamed()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "one\nagent edit\n");
        File.Delete(Path.Combine(worktree, "b.txt"));
        OrchestrationKit.Write(kit.Main, "a.txt", "one\ncommitted\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\ncommitted\n");
        OrchestrationKit.Write(kit.Main, "docs/with space.md", "new\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        OrchestrationKit.Write(worktree, "docs/with space.md", "the agent's\n");

        var refused = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true }, apply: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            "Agent 'ag' of 'o1' changed 3 of the path(s) to refresh - a.txt, b.txt, docs/with space.md - and refreshing would undo those changes. Nothing was copied.",
            refused.Message);
        Assert.Equal(
            [
                "  a.txt",
                "  b.txt",
                "  docs/with space.md",
                "To hand it every other path and leave these as it changed them, run again with --except <path> for each:",
                "  --except a.txt --except b.txt --except \"docs/with space.md\"",
                "--except says the path stays the agent's change, for its fold to weigh against what the main tree holds; it is not a --force.",
                $"log {kit.Layout.LogFile("ag")}",
            ],
            refused.Details);
        Assert.Equal("x\n", OrchestrationKit.Read(worktree, "docs/x.md"));

        var one = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true, Except = ["a.txt"] }, apply: true, Token);

        Assert.Equal(HarnessExit.Refused, one.ExitCode);
        Assert.Contains("changed 2 of the path(s) to refresh - b.txt, docs/with space.md -", one.Message, StringComparison.Ordinal);
        Assert.Contains("  --except a.txt --except b.txt --except \"docs/with space.md\"", one.Details!);
        Assert.Equal("x\n", OrchestrationKit.Read(worktree, "docs/x.md"));

        var request = new RefreshRequest { All = true, Except = ["a.txt", "b.txt", "docs/with space.md"] };
        var dry = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", request, apply: false, Token);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Equal(
            ["  docs/x.md", "3 path(s) it changed, named with --except, to leave as it changed them:", "  a.txt", "  b.txt", "  docs/with space.md"],
            dry.Details!.Take(5));
        Assert.Equal("x\n", OrchestrationKit.Read(worktree, "docs/x.md"));

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", request, apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("refreshed 1 path(s) into agent 'ag' of 'o1', recorded as handed to it, so its fold leaves them out", applied.Message);
        Assert.Equal(
            ["  docs/x.md", "3 path(s) it changed, named with --except, left as it changed them:", "  a.txt", "  b.txt", "  docs/with space.md"],
            applied.Details!.Take(5));
        Assert.Equal("x\ncommitted\n", OrchestrationKit.Read(worktree, "docs/x.md"));
        Assert.Equal("one\nagent edit\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.False(File.Exists(Path.Combine(worktree, "b.txt")));
        Assert.Equal("the agent's\n", OrchestrationKit.Read(worktree, "docs/with space.md"));
        Assert.DoesNotContain("a.txt", kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Weighed);

        var again = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", request, apply: true, Token);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Equal("agent 'ag' of 'o1' holds the main tree's copy of every other changed path", again.Message);
        Assert.Equal("3 path(s) it changed, named with --except, left as it changed them:", again.Details![0]);
    }

    /// <summary>
    /// An --except naming a path the refresh would not have refused - one the agent did not change, one the main tree did
    /// not move, or one outside the paths refreshed - leaves nothing out, and is refused as the typo it usually is, with
    /// nothing copied; one that could name nothing in the tree is a usage error.
    /// </summary>
    [Fact]
    public async Task AnExceptNamingNoPathTheRefreshWouldRefuse_IsRefused_AndNothingIsCopied()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "one\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nmain edit\n");

        var unchanged = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true, Except = ["a.txt", "docs/x.md", "b.txt"] }, apply: true, Token);

        Assert.Equal(HarnessExit.Refused, unchanged.ExitCode);
        Assert.Equal(
            "--except 'b.txt', 'docs/x.md' names no path to refresh that agent 'ag' of 'o1' changed, so it leaves nothing out: check its spelling. Nothing was copied.",
            unchanged.Message);
        Assert.Equal("x\n", OrchestrationKit.Read(worktree, "docs/x.md"));

        var outside = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { Paths = ["docs"], Except = ["a.txt"] }, apply: true, Token);

        Assert.Equal(HarnessExit.Refused, outside.ExitCode);
        Assert.StartsWith("--except 'a.txt' names no path to refresh under docs that agent 'ag' of 'o1' changed", outside.Message, StringComparison.Ordinal);
        Assert.Equal("x\n", OrchestrationKit.Read(worktree, "docs/x.md"));

        var usage = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true, Except = ["../x"] }, apply: true, Token);

        Assert.Equal(HarnessExit.UsageError, usage.ExitCode);
        Assert.Equal("--except, '../x', is not a path relative to the tree, spelt with forward slashes", usage.Message);
    }

    /// <summary>
    /// The command line carries both: --all for every path, and --except once for each path left as the agent changed it.
    /// </summary>
    [Fact]
    public async Task TheRefreshCommand_TakesAll_AndAnExceptForEachPath()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "one\nagent edit\n");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmain edit\n");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nmain edit\n");

        var refused = await CliRunner.RunAsync(["refresh-agent", "o1", "ag", "--all", "--apply"], Token, workingDirectory: kit.Main);
        var applied = await CliRunner.RunAsync(["refresh-agent", "o1", "ag", "--all", "--except", "a.txt", "--except", "b.txt", "--apply"], Token, workingDirectory: kit.Main);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("  --except a.txt --except b.txt", refused.StandardOutput + refused.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, applied.ExitCode);
        Assert.Equal("x\nmain edit\n", OrchestrationKit.Read(worktree, "docs/x.md"));
        Assert.Equal("one\nagent edit\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(worktree, "b.txt"));
    }

    /// <summary>
    /// What the main tree committed under the paths never moved between trees - the orchestrators' directory here, forced
    /// past its ignore rule - is never handed, committed or not, by seeding as by making.
    /// </summary>
    [Fact]
    public async Task WhatTheMainTreeCommittedOnTheFloor_IsNeverHanded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, ".orchestrators/note.md", "the orchestrator's\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\ncommitted\n");
        await kit.GitAsync(kit.Main, "add", "--force", ".orchestrators/note.md");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var seeded = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: false, Token);

        Assert.True(seeded.Succeeded, OrchestrationKit.Describe(seeded));
        Assert.Equal("seeded agent 'ag' of 'o1' with 1 path(s)", seeded.Message);
        Assert.Equal(["a.txt"], kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths.Keys);
        Assert.False(File.Exists(Path.Combine(worktree, ".orchestrators", "note.md")));
    }

    /// <summary>
    /// A path the main tree committed whose name is not UTF-8 refuses a refresh and a seeding, naming it, as an uncommitted
    /// one does: passed over, the agent would hold a stale copy while being told it held the main tree's.
    /// </summary>
    [Fact]
    public async Task APathTheMainTreeCommittedNamedOtherwiseThanInUtf8_RefusesTheHandOver_NamingIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        await kit.Harness.StageAsync(kit.Main, "\"docs/caf\\351.md\"", "bytes\n", Token);
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "a name no file here can hold");
        await kit.Harness.SkipWorktreeAsync(kit.Main, "\"docs/caf\\351.md\"", Token);

        var refreshed = await Assert.ThrowsAsync<HarnessException>(() => kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: false, Token));
        var seeded = await Assert.ThrowsAsync<HarnessException>(() => kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: false, Token));

        foreach (var refusal in new[] { refreshed, seeded })
        {
            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains(@"'docs/caf\351.md' is not named in UTF-8", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("it cannot be handed to an agent", refusal.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A path the main tree committed that no record of an agent can keep - one another platform reads as rooted - refuses a
    /// seeding, as an uncommitted one does: handed, it would be recorded as somewhere else on that platform.
    /// </summary>
    [Fact]
    public async Task APathTheMainTreeCommittedThatNoRecordCanKeep_RefusesSeeding()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows cannot hold a colon in a file name, and git there commits none.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "C:weird.md", "rooted on Windows\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a name another platform reads as rooted", Token);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: false, Token));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("no record of an agent can keep a path another platform would read as somewhere else", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// seed-agent says, as refresh-agent does, when the agent's base is not the main tree's HEAD; where git cannot say what
    /// the main tree's HEAD is, the refresh says that, and hands what it weighed all the same.
    /// </summary>
    [Fact]
    public async Task ABaseBehindTheMainTreesHead_IsSaidBySeeding_AndAHeadGitCannotReadIsSaidToBeOne()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = Core.Output.ReportText.Commit(kit.Record("ag").Base!);
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var head = Core.Output.ReportText.Commit((await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!);

        var seeded = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: false, Token);

        Assert.True(seeded.Succeeded, OrchestrationKit.Describe(seeded));
        Assert.Contains(
            $"its base {from} is not the main tree's HEAD {head}: what the main tree committed since reaches it as copies it is handed; "
            + "'dssharness rebase-agent o1 ag --apply' moves its base there",
            seeded.Details!);

        OrchestrationKit.Write(kit.Main, "a.txt", "one\nuncommitted\n");
        var main = kit.Main;
        var git = new InterceptingGitClient(kit.Harness.GitClient) { ResolveCommitFails = (directory, reference) => directory == main && reference == "HEAD" };

        var refreshed = await kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git).RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Contains("whether the main tree's HEAD is still its base cannot be told: git could not resolve 'HEAD': fatal: unable to read index", refreshed.Details!);
        Assert.Equal("one\nuncommitted\n", OrchestrationKit.Read(worktree, "a.txt"));
    }

    /// <summary>
    /// A file the main tree turned into a directory, and a directory it turned into a file, are handed as git holds them:
    /// the agent's file goes and what the directory holds comes, the agent's directory goes and the file comes - recorded as
    /// handed, so its fold leaves all of it out - on every seeding, never a hand-over that stops at the first.
    /// </summary>
    [Fact]
    public async Task SeedingAgain_HandsAFileTheMainTreeTurnedIntoADirectory_AndADirectoryItTurnedIntoAFile()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        File.Delete(Path.Combine(kit.Main, "a.txt"));
        OrchestrationKit.Write(kit.Main, "a.txt/inner.txt", "inner\n");
        Directory.Delete(Path.Combine(kit.Main, "docs"), recursive: true);
        OrchestrationKit.Write(kit.Main, "docs", "a file now\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var seeded = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: false, Token);

        Assert.True(seeded.Succeeded, OrchestrationKit.Describe(seeded));
        Assert.Equal("seeded agent 'ag' of 'o1' with 4 path(s)", seeded.Message);
        Assert.DoesNotContain(seeded.Details!, line => line.StartsWith("not handed:", StringComparison.Ordinal));
        Assert.Equal("inner\n", OrchestrationKit.Read(worktree, "a.txt/inner.txt"));
        Assert.Equal("a file now\n", OrchestrationKit.Read(worktree, "docs"));

        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.Equal(["a.txt/inner.txt", "docs"], seed.Paths.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["a.txt", "docs/x.md"], seed.Absent);
        Assert.Contains("4 inherited path(s) left out; 0 path(s) are its own:", (await kit.FoldAsync("ag", apply: false)).Details!);
    }

    /// <summary>
    /// A file the main tree has not committed that it turned into a directory is handed by a refresh as one it committed
    /// is, and a submodule's entry the main tree moved is named and never handed, as a repository of its own is.
    /// </summary>
    [Fact]
    public async Task Refreshing_HandsAFileTurnedIntoADirectoryUncommitted_AndNamesAMovedSubmodule()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var first = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;
        Directory.CreateDirectory(Path.Combine(kit.Main, "lib", "sub"));
        await kit.GitAsync(kit.Main, "update-index", "--add", "--cacheinfo", $"160000,{first},lib/sub");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "a submodule");
        var worktree = await kit.CreateAgentAsync("ag");
        await kit.GitAsync(kit.Main, "update-index", "--cacheinfo", $"160000,{kit.Record("ag").Base},lib/sub");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "the submodule moved");
        File.Delete(Path.Combine(kit.Main, "b.txt"));
        OrchestrationKit.Write(kit.Main, "b.txt/inner.txt", "inner\n");

        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("b.txt", "lib"), apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Equal(["  b.txt", "  b.txt/inner.txt"], refreshed.Details!.Take(2));
        Assert.Contains(refreshed.Details!, line => line.StartsWith("not handed: lib/sub - ", StringComparison.Ordinal));
        Assert.Equal("inner\n", OrchestrationKit.Read(worktree, "b.txt/inner.txt"));
        Assert.True(Directory.Exists(Path.Combine(worktree, "lib", "sub")));
    }

    /// <summary>
    /// A directory the main tree turned into a file it has not committed - which git's comparison with the agent's base
    /// never lists, untracked - is handed by a refresh, its files removed and the file copied, and the agent's copy of the
    /// directory, which holds no file at its path, is never taken for a change of its own that the file would write over.
    /// </summary>
    [Fact]
    public async Task Refreshing_HandsADirectoryTurnedIntoAFileUncommitted_OverTheAgentsCopyOfTheDirectory()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        Directory.Delete(Path.Combine(kit.Main, "docs"), recursive: true);
        OrchestrationKit.Write(kit.Main, "docs", "a file now\n");

        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Equal(["  docs", "  docs/x.md"], refreshed.Details!.Take(2));
        Assert.Equal("a file now\n", OrchestrationKit.Read(worktree, "docs"));
        Assert.Contains("2 inherited path(s) left out; 0 path(s) are its own:", (await kit.FoldAsync("ag", apply: false)).Details!);
    }

    /// <summary>
    /// What the agent holds of its own where a hand-over needs room - a file where the main tree holds a directory, a link
    /// to a directory elsewhere, a file of its own and a link in a directory the main tree turned into a file - refuses a
    /// refresh and a seeding, forced or not, naming each, before anything is written: a copy would write through the link,
    /// out of the agent's worktree, or stop at the file.
    /// </summary>
    [Fact]
    public async Task WhatTheAgentHoldsOfItsOwnWhereAHandOverNeedsRoom_RefusesIt_NamingEach()
    {
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "notes", "the agent's own notes\n");
        OrchestrationKit.Write(worktree, "docs/mine.md", "the agent's own\n");
        OrchestrationKit.Write(kit.Main, "notes/a.md", "the main tree's\n");
        OrchestrationKit.Write(kit.Main, "linked/b.md", "the main tree's\n");
        Directory.Delete(Path.Combine(kit.Main, "docs"), recursive: true);
        OrchestrationKit.Write(kit.Main, "docs", "a file now\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        TestLinks.DirectoryLink(Path.Combine(worktree, "linked"), elsewhere.Path);
        TestLinks.DirectoryLink(Path.Combine(worktree, "docs", "elsewhere"), elsewhere.Path);

        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs", "linked", "notes"), apply: true, Token);
        var seeded = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: true, Token);

        foreach (var refusal in new[] { refreshed, seeded })
        {
            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains("4 path(s) of its own - docs/elsewhere, docs/mine.md, linked and 1 more -", refusal.Message);
            Assert.EndsWith("Nothing was written.", refusal.Message, StringComparison.Ordinal);
        }

        Assert.Equal("the agent's own notes\n", OrchestrationKit.Read(worktree, "notes"));
        Assert.Equal("the agent's own\n", OrchestrationKit.Read(worktree, "docs/mine.md"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere.Path));
    }

    /// <summary>Seeding with nothing says, as any seeding does, that the agent's base is not the main tree's HEAD, and what moves it there.</summary>
    [Fact]
    public async Task SeedingWithNothing_SaysABaseBehindTheMainTreesHead()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var seeded = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: true, force: false, Token);

        Assert.True(seeded.Succeeded, OrchestrationKit.Describe(seeded));
        Assert.Contains(seeded.Details!, line => line.StartsWith($"its base {Core.Output.ReportText.Commit(kit.Record("ag").Base!)} is not the main tree's HEAD", StringComparison.Ordinal));
    }

    /// <summary>
    /// A path handed to the agent that the main tree then committed as it was handed is no change of the main tree's to the
    /// agent: the agent holds the main tree's copy, and a refresh hands nothing, though the commit moved it from the base.
    /// </summary>
    [Fact]
    public async Task APathTheMainTreeCommittedAsItWasHanded_IsNeverHandedAgain()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nhanded\n");
        await kit.CreateAgentAsync("ag");
        await kit.Harness.CommitAllAsync(kit.Main, "what was handed, committed", Token);

        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Equal("agent 'ag' of 'o1' holds the main tree's copy of every changed path under a.txt", refreshed.Message);
    }

    /// <summary>
    /// A symbolic link the main tree has not committed refuses a refresh and a seeding, as it refuses making an agent, before
    /// anything is written: handed, it would be the file it leads to.
    /// </summary>
    [Fact]
    public async Task ALinkTheMainTreeHasNotCommitted_RefusesARefreshAndASeeding()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        TestLinks.OrSkip(() => File.CreateSymbolicLink(Path.Combine(kit.Main, "docs", "linked.md"), "x.md"));

        var refreshed = await Assert.ThrowsAsync<HarnessException>(() => kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: true, Token));
        var seeded = await Assert.ThrowsAsync<HarnessException>(() => kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: false, Token));

        foreach (var refusal in new[] { refreshed, seeded })
        {
            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains("'docs/linked.md' is a symbolic link", refusal.Message);
        }

        Assert.False(File.Exists(Path.Combine(worktree, "docs", "linked.md")));
    }

    /// <summary>
    /// A name not in UTF-8 that the main tree committed outside the paths a refresh is given refuses nothing: only what the
    /// refresh weighs must be one a record can keep.
    /// </summary>
    [Fact]
    public async Task ANameNotInUtf8OutsideWhatARefreshWeighs_RefusesNothing()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        await kit.Harness.StageAsync(kit.Main, "\"src/caf\\351.md\"", "bytes\n", Token);
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\ncommitted\n");
        await kit.GitAsync(kit.Main, "add", "docs/x.md");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "a name no file here can hold, and a change");
        await kit.Harness.SkipWorktreeAsync(kit.Main, "\"src/caf\\351.md\"", Token);

        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Equal("x\ncommitted\n", OrchestrationKit.Read(worktree, "docs/x.md"));
    }

    /// <summary>The digest a seed records for the file at <paramref name="relative"/> under <paramref name="root"/>.</summary>
    private static async Task<string> DigestAsync(string root, string relative)
        => (await Core.FileSystem.FileContentHash.OfAsync(new HarnessFactory().FileSystem, Path.Combine(root, relative), Token)).Content;

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
