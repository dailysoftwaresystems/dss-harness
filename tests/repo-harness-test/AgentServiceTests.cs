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

    /// <summary>
    /// An agent folds paths - an edit of a file its base holds, an edit of a copy it was handed, a file it made - the main
    /// tree changes and commits them, its base is moved, and a refresh hands them: its fold recorded each as shared with
    /// what both trees then held, moving its base keeps what it shares as recorded, and its copy is still that, so the
    /// agent changed none. A consumer's steps, where every such path was refused as the agent's change: asked by the
    /// paths at the top of the tree, or for every path, with its base moved onto its own fold first - git then finds each
    /// copy no other than the main tree's file at its previous base, as was measured there - or left where the agent was
    /// made.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAgentFoldsPaths_TheMainTreeChangesAndCommitsThem_ItsBaseIsMoved_AndARefreshHandsThem_TheAgentChangedNone(bool ontoItsOwnFoldFirst)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nhanded\n");
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "one\nagent edit\n");
        OrchestrationKit.Write(worktree, "docs/x.md", "x\nhanded\nagent edit\n");
        OrchestrationKit.Write(worktree, "docs/made.md", "made\n");
        string[] folded = ["a.txt", "docs/made.md", "docs/x.md"];

        var fold = await kit.FoldAsync("ag", apply: true);

        Assert.True(fold.Succeeded, OrchestrationKit.Describe(fold));
        await kit.Harness.CommitAllAsync(kit.Main, "the agent's fold", Token);

        if (ontoItsOwnFoldFirst)
        {
            var onto = await kit.Harness.AgentService.RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token);

            Assert.True(onto.Succeeded, OrchestrationKit.Describe(onto));
        }

        // Two commits, as the folds of two siblings leave them, each changing what the agent's fold wrote.
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nagent edit\nsibling edit\n");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nhanded\nagent edit\nsibling edit\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a sibling's fold", Token);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nagent edit\nsibling edit\nanother\n");
        OrchestrationKit.Write(kit.Main, "docs/made.md", "made\nanother\n");
        await kit.Harness.CommitAllAsync(kit.Main, "another sibling's fold", Token);
        var previous = kit.Record("ag").Base!;

        var moved = await kit.Harness.AgentService.RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token);

        Assert.True(moved.Succeeded, OrchestrationKit.Describe(moved));
        Assert.Equal(
            [
                "0 path(s) the main tree committed since came into its worktree as git holds them:",
                "and 3 path(s) it shares with the main tree, handed to it or folded, stayed as its seed records them; refresh-agent hands it any the main tree moved since:",
                "  a.txt",
                "  docs/made.md",
                "  docs/x.md",
            ],
            moved.Details!.Take(5));
        Assert.Equal("one\nagent edit\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal("made\n", OrchestrationKit.Read(worktree, "docs/made.md"));
        Assert.Equal("x\nhanded\nagent edit\n", OrchestrationKit.Read(worktree, "docs/x.md"));

        if (ontoItsOwnFoldFirst)
        {
            // What was measured of each path refused: git finds its copy no other than the main tree's file at its previous base.
            await kit.GitAsync(worktree, ["diff", "--quiet", previous, "--", .. folded]);
        }

        var dry = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt", "b.txt", "docs"), apply: false, Token);
        var every = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true }, apply: false, Token);

        foreach (var outcome in new[] { dry, every })
        {
            Assert.True(outcome.Succeeded, OrchestrationKit.Describe(outcome));
            Assert.Equal("dry run: 3 path(s) would be refreshed into agent 'ag' of 'o1'; pass --apply to hand them over", outcome.Message);
            Assert.Equal(["  a.txt", "  docs/made.md", "  docs/x.md"], outcome.Details);
        }

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt", "b.txt", "docs"), apply: true, Token);
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("refreshed 3 path(s) into agent 'ag' of 'o1', recorded as handed to it, so its fold leaves them out", applied.Message);
        Assert.Equal("one\nagent edit\nsibling edit\nanother\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal("made\nanother\n", OrchestrationKit.Read(worktree, "docs/made.md"));
        Assert.Equal("x\nhanded\nagent edit\nsibling edit\n", OrchestrationKit.Read(worktree, "docs/x.md"));

        foreach (var path in folded)
        {
            Assert.Equal(await DigestAsync(kit.Main, path), seed.Paths[path]);
        }

        Assert.Contains("3 inherited path(s) left out; 0 path(s) are its own:", (await kit.FoldAsync("ag", apply: false)).Details!);
    }

    /// <summary>
    /// A path both an agent's later fold and a sibling's change met at, reconciled in the main tree by hand and named with
    /// --settled: the fold hands the agent the reconciliation and records it, so the agent's copy is the main tree's, its
    /// seed says so, and once the main tree is committed, its base moved onto it and the main tree moves the path again, a
    /// refresh hands it. (At 0.6.13 the fold recorded nothing, the copy by hand that followed left the seed naming what
    /// neither tree held, and the refresh refused the path as the agent's change.)
    /// </summary>
    [Fact]
    public async Task APathSettledByHand_WhichTheMainTreeThenMoves_IsHandedByARefresh()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nfirst pass\n");
        Assert.True((await kit.FoldAsync("ag", apply: true)).Succeeded);
        await kit.Harness.CommitAllAsync(kit.Main, "the agent's fold", Token);

        // A sibling's change and the agent's own meet at the path, and the two are reconciled in the main tree by hand.
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nfirst pass\nsibling edit\n");
        OrchestrationKit.Write(worktree, "b.txt", "two\nfirst pass\nsecond pass\n");
        var met = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, met.ExitCode);
        Assert.Contains(
            "  'b.txt': the main tree changed it after the agent was last handed it or folded, so writing the agent's copy would lose that change; a three-way merge "
            + "of the two conflicts in 1 place(s), shown below: reconcile it by hand, then --settled b.txt",
            met.Details!);
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nfirst pass\nsibling edit\nsecond pass\n");

        var settled = await kit.FoldAsync("ag", apply: true, "b.txt");

        Assert.True(settled.Succeeded, OrchestrationKit.Describe(settled));
        Assert.Contains("1 path(s) declared settled by hand: the main tree's copy handed to the agent and recorded as shared:", settled.Details!);
        Assert.Equal("two\nfirst pass\nsibling edit\nsecond pass\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal(await DigestAsync(kit.Main, "b.txt"), kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths["b.txt"]);

        // The commit of what was folded, and the agent's base moved onto it; then a sibling's change of the path, committed.
        await kit.Harness.CommitAllAsync(kit.Main, "the agent's second fold, reconciled", Token);
        var onto = await kit.Harness.AgentService.RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token);

        Assert.True(onto.Succeeded, OrchestrationKit.Describe(onto));

        OrchestrationKit.Write(kit.Main, "b.txt", "two\nfirst pass\nsibling edit\nsecond pass\nanother sibling\n");
        await kit.Harness.CommitAllAsync(kit.Main, "another sibling's fold", Token);
        var moved = await kit.Harness.AgentService.RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token);

        Assert.True(moved.Succeeded, OrchestrationKit.Describe(moved));
        Assert.Contains("and 1 path(s) it shares with the main tree, handed to it or folded, stayed as its seed records them; refresh-agent hands it any the main tree moved since:", moved.Details!);

        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("b.txt"), apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Equal("two\nfirst pass\nsibling edit\nsecond pass\nanother sibling\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal(await DigestAsync(kit.Main, "b.txt"), kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths["b.txt"]);
    }

    /// <summary>
    /// A path the main tree moved that the agent already holds as the main tree does - here one its seed records as its own
    /// fold wrote it, and one it shares nothing for, each brought to the main tree's bytes by hand - is recorded as shared,
    /// listed, and written only with --apply: a dry run says so and records nothing. Its record then says what it holds, so
    /// once the main tree moves both again a refresh hands them. (At 0.6.13 such a path was dropped unrecorded, and that
    /// next refresh refused both as the agent's changes.)
    /// </summary>
    [Fact]
    public async Task APathTheAgentAlreadyHoldsAsTheMainTreeDoes_IsRecordedAsShared_WithApplyOnly_AndOnceTheMainTreeMovesItIsHanded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        Assert.True((await kit.FoldAsync("ag", apply: true)).Succeeded);
        var recorded = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths["b.txt"];

        OrchestrationKit.Write(kit.Main, "a.txt", "one\nsibling edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nagent edit\nsibling edit\n");
        File.Copy(Path.Combine(kit.Main, "a.txt"), Path.Combine(worktree, "a.txt"), overwrite: true);
        File.Copy(Path.Combine(kit.Main, "b.txt"), Path.Combine(worktree, "b.txt"), overwrite: true);
        string[] listed = ["  a.txt", "  b.txt"];

        var dry = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true }, apply: false, Token);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Equal("agent 'ag' of 'o1' holds the main tree's copy of every changed path; 2 of them are not recorded as shared yet: pass --apply to record them", dry.Message);
        Assert.Equal(["2 path(s) it already holds as the main tree does, to record as shared:", .. listed], dry.Details);
        Assert.Equal(recorded, kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths["b.txt"]);
        Assert.DoesNotContain("a.txt", kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths.Keys);

        var alike = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true }, apply: true, Token);
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.True(alike.Succeeded, OrchestrationKit.Describe(alike));
        Assert.Equal("agent 'ag' of 'o1' holds the main tree's copy of every changed path, and recorded 2 of them as shared", alike.Message);
        Assert.Equal(["2 path(s) it already holds as the main tree does, recorded as shared:", .. listed], alike.Details!.Take(3));
        Assert.Equal(await DigestAsync(kit.Main, "a.txt"), seed.Paths["a.txt"]);
        Assert.Equal(await DigestAsync(kit.Main, "b.txt"), seed.Paths["b.txt"]);
        Assert.Equal("agent 'ag' of 'o1' holds the main tree's copy of every changed path", (await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true }, apply: false, Token)).Message);

        OrchestrationKit.Write(kit.Main, "a.txt", "one\nsibling edit\nanother\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nagent edit\nsibling edit\nanother\n");

        var handed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true }, apply: true, Token);

        Assert.True(handed.Succeeded, OrchestrationKit.Describe(handed));
        Assert.Equal("refreshed 2 path(s) into agent 'ag' of 'o1', recorded as handed to it, so its fold leaves them out", handed.Message);
        Assert.Equal("one\nsibling edit\nanother\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal("two\nagent edit\nsibling edit\nanother\n", OrchestrationKit.Read(worktree, "b.txt"));
    }

    /// <summary>
    /// A refresh that hands some paths and finds the agent already holding others as the main tree does says both, in its
    /// dry run and once applied, and records both.
    /// </summary>
    [Fact]
    public async Task ARefreshHandingSomePaths_AndFindingOthersAlike_SaysAndRecordsBoth()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmain edit\n");
        File.Copy(Path.Combine(kit.Main, "b.txt"), Path.Combine(worktree, "b.txt"), overwrite: true);

        var dry = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt", "b.txt"), apply: false, Token);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Equal("dry run: 1 path(s) would be refreshed into agent 'ag' of 'o1', and 1 recorded as shared; pass --apply to hand them over", dry.Message);
        Assert.Equal(["  a.txt", "1 path(s) it already holds as the main tree does, to record as shared:", "  b.txt"], dry.Details);

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt", "b.txt"), apply: true, Token);
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal(
            "refreshed 1 path(s) into agent 'ag' of 'o1', recorded as handed to it, so its fold leaves them out, and recorded 1 it already held as the main tree does",
            applied.Message);
        Assert.Equal("one\nmain edit\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal(await DigestAsync(kit.Main, "a.txt"), seed.Paths["a.txt"]);
        Assert.Equal(await DigestAsync(kit.Main, "b.txt"), seed.Paths["b.txt"]);
    }

    /// <summary>
    /// A file the agent's session writes between the weighing and the copy is never written over: the refresh stops there,
    /// exit 21, naming it, and the agent's file holds what its session wrote. (At 0.6.13 it was written over.)
    /// </summary>
    [Fact]
    public async Task AFileTheAgentChangedAfterItWasWeighed_StopsTheRefresh_AndIsNotWrittenOver()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var copy = Path.Combine(worktree, "a.txt");

        // Its fourth reading is the one that asks, before the copy, whether it is what was weighed: the session writes first.
        var agents = kit.Harness.Agents(
            new AgentFoldMergeTests.MeanwhileFileSystem(kit.Harness.FileSystem, copy, 4, () => File.WriteAllText(copy, "one\nthe session's edit\n")),
            kit.Harness.AnchorRegistryService);

        var stopped = await agents.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Equal(
            "Refreshing agent 'ag' of 'o1' stopped after 0 of 1 path(s): 'a.txt' holds other content in the agent's worktree now than when it was measured, so it "
            + "was not written over. Its seed records the 0 handed; run 'dssharness refresh-agent o1 ag a.txt --apply' again once that is dealt with.",
            stopped.Message);
        Assert.Equal("one\nthe session's edit\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.DoesNotContain("a.txt", kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths.Keys);
    }

    /// <summary>seed-agent's hand-over is guarded alike: a file written after it was weighed stops it there, exit 21, never written over.</summary>
    [Fact]
    public async Task AFileTheAgentChangedAfterItWasWeighed_StopsSeeding_AndIsNotWrittenOver()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var copy = Path.Combine(worktree, "a.txt");
        var agents = kit.Harness.Agents(
            new AgentFoldMergeTests.MeanwhileFileSystem(kit.Harness.FileSystem, copy, 2, () => File.WriteAllText(copy, "one\nthe session's edit\n")),
            kit.Harness.AnchorRegistryService);

        var stopped = await agents.SeedAsync(kit.Main, "o1", "ag", empty: false, force: false, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith(
            "Seeding agent 'ag' of 'o1' stopped after 0 of 1 path(s): 'a.txt' holds other content in the agent's worktree now than when it was measured, so it was not written over.",
            stopped.Message,
            StringComparison.Ordinal);
        Assert.Equal("one\nthe session's edit\n", OrchestrationKit.Read(worktree, "a.txt"));
    }

    /// <summary>
    /// A file the agent's session makes runnable between the weighing and the copy - its content unchanged - is never
    /// written over either: the guard asks for the file whole, its digest and its bit, and the refresh stops there
    /// naming what changed.
    /// </summary>
    [Fact]
    public async Task AFileTheAgentsSessionMadeRunnableAfterItWasWeighed_StopsTheRefresh_AndIsNotWrittenOver()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit with a file.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var copy = Path.Combine(worktree, "a.txt");

        // Its fourth reading is the one that asks, before the copy, whether it is what was weighed: the session flips the bit first.
        var agents = kit.Harness.Agents(
            new AgentFoldMergeTests.MeanwhileFileSystem(kit.Harness.FileSystem, copy, 4, () => kit.Harness.FilePermissions.SetExecutable(copy, executable: true)),
            kit.Harness.AnchorRegistryService);

        var stopped = await agents.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Equal(
            "Refreshing agent 'ag' of 'o1' stopped after 0 of 1 path(s): 'a.txt' can now be run in the agent's worktree, which it could not when it was measured, so it "
                + "was not written over. Its seed records the 0 handed; run 'dssharness refresh-agent o1 ag a.txt --apply' again once that is dealt with.",
            stopped.Message);
        Assert.Equal("one\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.True(kit.Harness.FilePermissions.IsExecutable(copy), "The session's change of the file was written over.");
    }

    /// <summary>
    /// A deletion is guarded as a copy is: a file the main tree deleted that the agent's session rewrote after it was
    /// weighed is not removed, and the refresh stops there naming it.
    /// </summary>
    [Fact]
    public async Task ADeletionTheAgentChangedAfterItWasWeighed_StopsTheRefresh_AndIsNotRemoved()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        File.Delete(Path.Combine(kit.Main, "b.txt"));
        var copy = Path.Combine(worktree, "b.txt");

        // Its fourth reading is the one that asks, before the removal, whether it is what was weighed: the session writes first.
        var agents = kit.Harness.Agents(
            new AgentFoldMergeTests.MeanwhileFileSystem(kit.Harness.FileSystem, copy, 4, () => File.WriteAllText(copy, "two\nthe session's edit\n")),
            kit.Harness.AnchorRegistryService);

        var stopped = await agents.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("b.txt"), apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith(
            "Refreshing agent 'ag' of 'o1' stopped after 0 of 1 path(s): 'b.txt' holds other content in the agent's worktree now than when it was measured, so it was not removed.",
            stopped.Message,
            StringComparison.Ordinal);
        Assert.Equal("two\nthe session's edit\n", OrchestrationKit.Read(worktree, "b.txt"));
    }

    /// <summary>
    /// A path the agent already holds as the main tree does is recorded as shared with the bit it was measured with, not
    /// the bit its session gives it meanwhile: the record then says what both trees held, and the next refresh leaves the
    /// session's change of the bit as the agent's own instead of weighing the path moved and silently undoing it.
    /// </summary>
    [Fact]
    public async Task APathAlreadyAlike_IsRecordedWithTheBitItWasMeasuredWith_AndTheSessionChangeStaysTheAgents()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit with a file.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nalike\n");
        OrchestrationKit.Write(kit.Main, "z.txt", "zed\nalike\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmain edit\n");
        File.Copy(Path.Combine(kit.Main, "a.txt"), Path.Combine(worktree, "a.txt"), overwrite: true);
        File.Copy(Path.Combine(kit.Main, "z.txt"), Path.Combine(worktree, "z.txt"), overwrite: true);
        var alike = Path.Combine(worktree, "a.txt");

        // The second reading of z.txt is the one that follows a.txt's measuring read: the session makes a.txt runnable in
        // between, after it was weighed and before the refresh records it.
        var agents = kit.Harness.Agents(
            new AgentFoldMergeTests.MeanwhileFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "z.txt"), 2, () => kit.Harness.FilePermissions.SetExecutable(alike, executable: true)),
            kit.Harness.AnchorRegistryService);

        var applied = await agents.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true }, apply: true, Token);
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal(await DigestAsync(kit.Main, "a.txt"), seed.Paths["a.txt"]);
        Assert.DoesNotContain("a.txt", seed.Executable ?? []);

        var again = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true }, apply: true, Token);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.True(kit.Harness.FilePermissions.IsExecutable(alike), "The agent's change of the bit was undone.");
    }

    /// <summary>
    /// A path the main tree already holds as the agent does is recorded by the fold with the bit it was measured with, not
    /// the bit its session gives it meanwhile: nothing is written over an already-in path and no guard runs there, and the
    /// record then says what both trees held as it was measured - so the agent's later change of the bit stays its own, to
    /// fold like any other change, instead of being silently absorbed.
    /// </summary>
    [Fact]
    public async Task APathAlreadyInTheMainTree_IsRecordedByTheFoldWithTheBitItWasMeasuredWith()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit with a file.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nalike\n");
        OrchestrationKit.Write(kit.Main, "z.txt", "zed\nalike\n");
        File.Copy(Path.Combine(kit.Main, "a.txt"), Path.Combine(worktree, "a.txt"), overwrite: true);
        File.Copy(Path.Combine(kit.Main, "z.txt"), Path.Combine(worktree, "z.txt"), overwrite: true);
        var alike = Path.Combine(worktree, "a.txt");

        // A fold reads the agent's z.txt once, as it weighs it, after a.txt - paths are weighed in order - and records both
        // only after it weighed them all: the session makes a.txt runnable then, after it was weighed and before the fold
        // records it.
        var agents = kit.Harness.Agents(
            new AgentFoldMergeTests.MeanwhileFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "z.txt"), 1, () => kit.Harness.FilePermissions.SetExecutable(alike, executable: true)),
            kit.Harness.AnchorRegistryService);

        var folded = await agents.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, Token);
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.True(kit.Harness.FilePermissions.IsExecutable(alike), "The session did not make a.txt runnable while the fold ran.");
        Assert.Equal(await DigestAsync(kit.Main, "a.txt"), seed.Paths["a.txt"]);
        Assert.DoesNotContain("a.txt", seed.Executable ?? []);

        var again = await kit.Harness.AgentService.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, Token);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.True(kit.Harness.FilePermissions.IsExecutable(Path.Combine(kit.Main, "a.txt")), "The agent's change of the bit was not folded.");
    }

    /// <summary>
    /// A dry run that could not read what it weighs keeps its failure's code where a run also holds what its --apply
    /// takes: what cannot be read stands above a holder in the plan's order, and the dry run's lines name the holder
    /// either way, never turning the failure into a refusal.
    /// </summary>
    [Fact]
    public async Task ADryRunThatCouldNotReadWhatItWeighs_KeepsItsOwnCode_AndStillNamesTheRunHoldingWhatItsApplyTakes()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        await using var held = await HoldTheWorktreeAsync(kit, worktree);

        var agents = kit.Harness.Agents(new UnreadableFileSystem(kit.Harness.FileSystem, Path.Combine(kit.Main, "a.txt")), kit.Harness.AnchorRegistryService);
        var dry = await agents.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true }, apply: false, Token);

        Assert.Equal(HarnessExit.CommandFailed, dry.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' was not refreshed: what it or the main tree holds cannot be read", dry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("a run also holds", dry.Message, StringComparison.Ordinal);
        Assert.Contains(dry.Details!, line => line.StartsWith($"held now: '{worktree}' on local is held by ", StringComparison.Ordinal));
    }

    /// <summary>Takes the worktree at <paramref name="path"/> whole for a run of the test, for as long as the caller holds the attempt.</summary>
    private static async Task<Core.Execution.RunLockHandle> HoldTheWorktreeAsync(OrchestrationKit kit, string path)
    {
        var attempt = await kit.Harness.RunLock
            .TryAcquireAsync(new Core.Repository.HarnessLayout(kit.Main, kit.Main), new Core.Execution.LockRequest { Host = "local", Tree = path, Scope = Core.Execution.LockScope.TreeExclusive, RunId = Core.Execution.RunId.New(), Command = "test" }, Token);

        return attempt.Handle ?? throw new InvalidOperationException($"the test could not hold '{path}': {attempt.HeldBy}");
    }

    /// <summary>
    /// A refresh that stops in its hand-over merges nothing, though every merge is clean and named: each merge is
    /// written only once everything else is handed, so nothing of the agent's is written over while paths it was to be
    /// handed still stand - and what stopped it is said with both counts.
    /// </summary>
    [Fact]
    public async Task ARefreshThatStopsInItsHandOver_MergesNothing()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "m1.txt", "one\ntwo\n");
        OrchestrationKit.Write(kit.Main, "m2.txt", "a\nb\n");
        await kit.Harness.CommitAllAsync(kit.Main, "two files both trees will change", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        OrchestrationKit.Write(worktree, "m1.txt", "ONE\ntwo\n");
        OrchestrationKit.Write(kit.Main, "m1.txt", "one\ntwo\nTHREE\n");
        OrchestrationKit.Write(worktree, "m2.txt", "A\nb\n");
        OrchestrationKit.Write(kit.Main, "m2.txt", "a\nb\nC\n");
        var agents = kit.Harness.Agents(
            new AgentFoldMergeTests.UnwritableFile(kit.Harness.FileSystem, Path.Combine(worktree, "a.txt")),
            kit.Harness.AnchorRegistryService);

        var stopped = await agents.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true, Merge = ["m1.txt", "m2.txt"] }, apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith("Refreshing agent 'ag' of 'o1' stopped after 0 of 1 path(s) and merging 0 of 2: ", stopped.Message);
        Assert.Equal("one\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal("ONE\ntwo\n", OrchestrationKit.Read(worktree, "m1.txt"));
        Assert.Equal("A\nb\n", OrchestrationKit.Read(worktree, "m2.txt"));
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;
        Assert.False(seed.Paths.ContainsKey("m1.txt"));
        Assert.False(seed.Paths.ContainsKey("m2.txt"));
    }

    /// <summary>
    /// A refresh that stops among its merges has handed and recorded everything else: the paths it handed are in its
    /// seed, each merge that finished is the agent's change recorded as any merge is, and only what stopped stands in
    /// the way of the command run again.
    /// </summary>
    [Fact]
    public async Task ARefreshThatStopsAmongItsMerges_HandsAndRecordsEverythingElse()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "m1.txt", "one\ntwo\n");
        OrchestrationKit.Write(kit.Main, "m2.txt", "a\nb\n");
        await kit.Harness.CommitAllAsync(kit.Main, "two files both trees will change", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        OrchestrationKit.Write(worktree, "m1.txt", "ONE\ntwo\n");
        OrchestrationKit.Write(kit.Main, "m1.txt", "one\ntwo\nTHREE\n");
        OrchestrationKit.Write(worktree, "m2.txt", "A\nb\n");
        OrchestrationKit.Write(kit.Main, "m2.txt", "a\nb\nC\n");
        var agents = kit.Harness.Agents(
            new AgentFoldMergeTests.UnwritableFile(kit.Harness.FileSystem, Path.Combine(worktree, "m2.txt")),
            kit.Harness.AnchorRegistryService);

        var stopped = await agents.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true, Merge = ["m1.txt", "m2.txt"] }, apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith("Refreshing agent 'ag' of 'o1' stopped after 1 of 1 path(s) and merging 1 of 2: ", stopped.Message);
        Assert.Equal("one\nmain edit\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal("ONE\ntwo\nTHREE\n", OrchestrationKit.Read(worktree, "m1.txt"));
        Assert.Equal("A\nb\n", OrchestrationKit.Read(worktree, "m2.txt"));
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;
        Assert.Equal(await DigestAsync(kit.Main, "a.txt"), seed.Paths["a.txt"]);
        Assert.Equal(await DigestAsync(kit.Main, "m1.txt"), seed.Paths["m1.txt"]);
        Assert.False(seed.Paths.ContainsKey("m2.txt"));
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
        // Each path both trees changed says what a merge of the two gives, and the conflicts are shown under the paths.
        Assert.Equal(
            [
                "  a.txt   (a three-way merge of the two conflicts in 1 place(s), shown below)",
                "  b.txt",
                "  docs/with space.md   (no three-way merge is offered: both trees made it, and neither held a file there before)",
            ],
            refused.Details!.Take(3));
        Assert.StartsWith("conflicts of 'a.txt', 1 place(s) - as the main tree holds it, as both trees held it, as the agent holds it:", refused.Details![3], StringComparison.Ordinal);
        Assert.Equal(
            [
                "To hand it every other path and leave these as it changed them, run again with --except <path> for each:",
                "  --except a.txt --except b.txt --except \"docs/with space.md\"",
                "--except says the path stays the agent's change, for its fold to weigh against what the main tree holds; it is not a --force.",
                $"log {kit.Layout.LogFile("ag")}",
            ],
            refused.Details.TakeLast(4));
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
    /// A refresh that stops part way says the line that asks for it again with everything it was asked - its paths, or
    /// --all, and each path left as the agent changed it. Said bare, that line asked for another refresh: of the
    /// registries' directory alone, and refused over the very changes this one was told to leave.
    /// </summary>
    [Theory]
    [InlineData(true, "--all --except a.txt")]
    [InlineData(false, "a.txt b.txt docs --except a.txt")]
    public async Task ARefreshThatStopsPartWay_SaysTheLineThatAsksForItAgain_WithEverythingItWasAsked(bool all, string asked)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "one\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\ncommitted\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var agents = kit.Harness.Agents(new UncopyableFileSystem(kit.Harness.FileSystem, Path.Combine(kit.Main, "docs", "x.md")), kit.Harness.AnchorRegistryService);
        var request = all ? new RefreshRequest { All = true, Except = ["a.txt"] } : new RefreshRequest { Paths = ["a.txt", "b.txt", "docs"], Except = ["a.txt"] };

        var stopped = await agents.RefreshAsync(kit.Main, "o1", "ag", request, apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith("Refreshing agent 'ag' of 'o1' stopped after 1 of 2 path(s): ", stopped.Message, StringComparison.Ordinal);
        Assert.EndsWith(
            $"Its seed records the 1 handed; run 'dssharness refresh-agent o1 ag {asked} --apply' again once that is dealt with.",
            stopped.Message,
            StringComparison.Ordinal);
        Assert.Equal("one\nagent edit\n", OrchestrationKit.Read(worktree, "a.txt"));
    }

    /// <summary>The arguments that ask for a refresh again are its own, each as a command line takes it: none for the registries' directory.</summary>
    [Fact]
    public void TheArgumentsThatAskForARefreshAgain_AreItsOwn()
    {
        Assert.Equal(string.Empty, new RefreshRequest().Arguments());
        Assert.Equal("--all", new RefreshRequest { All = true }.Arguments());
        Assert.Equal("docs \"with space\"", RefreshRequest.Under("docs", "with space").Arguments());
        Assert.Equal("docs --except a.txt --except \"b c.txt\"", new RefreshRequest { Paths = ["docs"], Except = ["a.txt", "b c.txt"] }.Arguments());
        Assert.Equal("--all --except a.txt --stale b.txt --stale \"c d.txt\"", new RefreshRequest { All = true, Except = ["a.txt"], Stale = ["b.txt", "c d.txt"] }.Arguments());
        Assert.Equal("src --stale b.txt --merge \"c d.txt\"", new RefreshRequest { Paths = ["src"], Stale = ["b.txt"], Merge = ["c d.txt"] }.Arguments());
    }

    /// <summary>
    /// A copy the agent holds that is the main tree's own file at an earlier commit is said to be one in the refusal over
    /// its changes, with that commit and the arguments that hand it the main tree's copy instead; named with --stale, it is
    /// handed like any other path, listed apart with its commit, and recorded as handed.
    /// </summary>
    [Fact]
    public async Task ACopyThatIsTheMainTreesFileAtAnEarlierCommit_IsSaidToBeOne_AndNamedWithStale_IsHandedAndRecorded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var first = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nsecond\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the second version", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "one\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nthird\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the third version", Token);
        var at = $"its bytes are the main tree's at {Core.Output.ReportText.Commit(first)}";

        var refused = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            "Agent 'ag' of 'o1' changed 1 of the path(s) to refresh - a.txt - and refreshing would undo those changes. Nothing was copied.",
            refused.Message);
        Assert.StartsWith($"  a.txt   ({at}; a three-way merge of the two conflicts in 1 place(s), shown below)", refused.Details![0], StringComparison.Ordinal);
        Assert.Equal(
            [
                "To hand it every other path and leave these as it changed them, run again with --except <path> for each:",
                "  --except a.txt",
                "1 of them holds bytes the main tree itself held at that path: copies gone stale, where nobody changed them on purpose. To hand it the "
                    + "main tree's copy of each instead, name them:",
                "  --stale a.txt",
                "--except says the path stays the agent's change, for its fold to weigh against what the main tree holds; --stale says its copy is "
                    + "not the agent's work, and is taken only where the main tree's history holds those very bytes at that path. None is a --force.",
                $"log {kit.Layout.LogFile("ag")}",
            ],
            refused.Details.SkipWhile(line => !line.StartsWith("To hand it", StringComparison.Ordinal)));

        var request = new RefreshRequest { Paths = ["a.txt"], Stale = ["a.txt"] };
        var dry = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", request, apply: false, Token);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Equal("dry run: 1 path(s) would be refreshed into agent 'ag' of 'o1'; pass --apply to hand them over", dry.Message);
        Assert.Equal(
            ["1 path(s) named with --stale, whose copy is the main tree's own at an earlier commit, to hand the main tree's copy:", $"  a.txt   ({at})"],
            dry.Details!.Take(2));
        Assert.Equal("one\n", OrchestrationKit.Read(worktree, "a.txt"));

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", request, apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal(
            ["1 path(s) named with --stale, whose copy is the main tree's own at an earlier commit, handed the main tree's copy:", $"  a.txt   ({at})"],
            applied.Details!.Take(2));
        Assert.Equal("one\nthird\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal(await DigestAsync(kit.Main, "a.txt"), kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths["a.txt"]);
    }

    /// <summary>
    /// Where the main tree's history holds the agent's very bytes at a path across several commits in a row - another
    /// path's commits among them - the commit named is the newest of the run: what a person finds first in the log is
    /// what the line says the main tree holds.
    /// </summary>
    [Fact]
    public async Task ACopyTheMainTreeHeldAtSeveralEqualCommits_IsNamedByTheNewest()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nsecond\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the second version of the first", Token);
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nsecond\n");
        await kit.Harness.CommitAllAsync(kit.Main, "another path, the first unchanged", Token);
        var equal = await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nthird\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the third version of the first", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nfourth\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nthird\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the fourth version of the first", Token);

        // The agent puts both copies back to versions below its base: its own changes, and stale.
        OrchestrationKit.Write(worktree, "a.txt", "one\nsecond\n");
        OrchestrationKit.Write(worktree, "b.txt", "two\n");

        var refused = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt", "b.txt"), apply: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith($"  a.txt   (its bytes are the main tree's at {Core.Output.ReportText.Commit(equal!)}", refused.Details![0], StringComparison.Ordinal);
        Assert.StartsWith("  b.txt   (its bytes are the main tree's at ", refused.Details![1], StringComparison.Ordinal);
        Assert.Equal("one\nsecond\n", OrchestrationKit.Read(worktree, "a.txt"));
    }

    /// <summary>
    /// A --stale naming a path the main tree holds a link or a directory git will not look into at names neither a stale
    /// copy nor a hand-over: the path is named as itself among what is not handed, and nothing fails - where reading the
    /// main tree's copy as a file would have failed the whole command, and weighing it as a stale copy would have said
    /// both that it is handed and that it is not.
    /// </summary>
    [Fact]
    public async Task AStaleNameForAPathTheMainTreeHoldsALinkOrADirectoryAt_IsNamedAsItself_NeverStale()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nsecond\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the second version", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "one\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nthird\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the third version", Token);
        File.Delete(Path.Combine(kit.Main, "a.txt"));
        TestLinks.OrSkip(() => File.CreateSymbolicLink(Path.Combine(kit.Main, "a.txt"), Path.Combine(kit.Main, "docs", "x.md")));
        await kit.Harness.CommitAllAsync(kit.Main, "a link where the file was", Token);

        var refreshed = await kit.Harness.AgentService.RefreshAsync(
            kit.Main, "o1", "ag", new RefreshRequest { All = true, Stale = ["a.txt"] }, apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Contains(refreshed.Details!, line => line.StartsWith("not handed: a.txt - a symbolic link the main tree committed", StringComparison.Ordinal));
        Assert.DoesNotContain(refreshed.Details!, line => line.StartsWith("1 path(s) named with --stale", StringComparison.Ordinal));

        using var other = new TempDirectory();
        kit = await OrchestrationKit.PrepareAsync(other);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nsecond\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the second version", Token);
        worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "one\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nthird\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the third version", Token);
        File.Delete(Path.Combine(kit.Main, "a.txt"));
        await kit.Harness.InitializeGitRepositoryAsync(Directory.CreateDirectory(Path.Combine(kit.Main, "a.txt")).FullName, Token);

        var unlooked = await kit.Harness.AgentService.RefreshAsync(
            kit.Main, "o1", "ag", new RefreshRequest { All = true, Stale = ["a.txt"] }, apply: true, Token);

        Assert.True(unlooked.Succeeded, OrchestrationKit.Describe(unlooked));
        Assert.Contains(unlooked.Details!, line => line.StartsWith("not handed: a.txt - a directory git will not look into", StringComparison.Ordinal));
        Assert.DoesNotContain(unlooked.Details!, line => line.StartsWith("1 path(s) named with --stale", StringComparison.Ordinal));
    }

    /// <summary>
    /// A --stale naming a path whose copy in the agent is a link is refused naming it - the agent holds no file there -
    /// never read as the file the link leads to and never failing the whole command.
    /// </summary>
    [Fact]
    public async Task AStaleNameForAPathWhoseAgentCopyIsALink_IsRefusedNamingIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nsecond\n");
        File.Delete(Path.Combine(worktree, "a.txt"));
        TestLinks.OrSkip(() => File.CreateSymbolicLink(Path.Combine(worktree, "a.txt"), Path.Combine(worktree, "b.txt")));

        var refused = await kit.Harness.AgentService.RefreshAsync(
            kit.Main, "o1", "ag", new RefreshRequest { All = true, Stale = ["a.txt"] }, apply: true, Token);

        Assert.True(refused.ExitCode == HarnessExit.Refused, OrchestrationKit.Describe(refused));
        Assert.Equal("--stale 'a.txt': the agent holds no file there, and a deletion is its change. --except leaves it. Nothing was copied.", refused.Message);
    }

    /// <summary>
    /// The proof is git's: the agent's copy is weighed as git would store it, so one that differs from an earlier commit's
    /// file only by a line-ending conversion is that commit's file, and is handed as a stale copy.
    /// </summary>
    [Fact]
    public async Task ACopyDifferingFromAnEarlierCommitsFileOnlyInItsLineEndings_IsThatCommitsFile()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, ".gitattributes", "*.txt text eol=lf\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nsecond\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the second version, and how text is stored", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "one\r\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nthird\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the third version", Token);

        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { Paths = ["a.txt"], Stale = ["a.txt"] }, apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("one\nthird\n", OrchestrationKit.Read(worktree, "a.txt"));
    }

    /// <summary>
    /// A --stale nothing proves is refused, nothing copied: a copy the main tree's history does not hold at that path, and
    /// a path the agent holds no file at, whose deletion is its change; several said together. One naming no path under
    /// those refreshed names nothing, and one naming a path also named with --except is a usage error.
    /// </summary>
    [Fact]
    public async Task AStaleNothingProves_IsRefused_AndNothingIsCopied()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "never committed\n");
        File.Delete(Path.Combine(worktree, "b.txt"));
        OrchestrationKit.Write(kit.Main, "a.txt", "one\ncommitted\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        const string Unproved = "--stale 'a.txt': its copy is no file the main tree held at that path in the last 512 commit(s) that changed it, so "
            + "nothing proves it stale. --except leaves it as the agent's change, for its fold to weigh.";
        const string Deleted = "--stale 'b.txt': the agent holds no file there, and a deletion is its change. --except leaves it.";

        async Task<CommandOutcome> RefreshAsync(RefreshRequest request)
        {
            var outcome = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", request, apply: true, Token);

            Assert.Equal("never committed\n", OrchestrationKit.Read(worktree, "a.txt"));
            Assert.Equal("x\n", OrchestrationKit.Read(worktree, "docs/x.md"));
            return outcome;
        }

        var unproved = await RefreshAsync(new RefreshRequest { All = true, Except = ["b.txt"], Stale = ["a.txt"] });
        var deleted = await RefreshAsync(new RefreshRequest { All = true, Except = ["a.txt"], Stale = ["b.txt"] });
        var both = await RefreshAsync(new RefreshRequest { All = true, Stale = ["a.txt", "b.txt"] });
        var stray = await RefreshAsync(new RefreshRequest { Paths = ["docs"], Stale = ["a.txt"] });
        var twice = await RefreshAsync(new RefreshRequest { All = true, Except = ["a.txt"], Stale = ["a.txt"] });

        Assert.Equal((HarnessExit.Refused, $"{Unproved} Nothing was copied."), (unproved.ExitCode, unproved.Message));
        Assert.Equal((HarnessExit.Refused, $"{Deleted} Nothing was copied."), (deleted.ExitCode, deleted.Message));
        Assert.Equal((HarnessExit.Refused, "2 path(s) named with --stale are not proved stale, each said below. Nothing was copied."), (both.ExitCode, both.Message));
        Assert.Equal([$"  {Unproved}", $"  {Deleted}"], both.Details!.Take(2));
        Assert.Equal(
            (HarnessExit.Refused, "--stale 'a.txt' names no path under docs whose copy in the agent is not the main tree's, so it hands nothing: check its spelling. Nothing was copied."),
            (stray.ExitCode, stray.Message));
        Assert.Equal(
            (HarnessExit.UsageError, "'a.txt' is named with both --except and --stale: a path is left as the agent changed it, or handed the main tree's copy, never both."),
            (twice.ExitCode, twice.Message));
    }

    /// <summary>
    /// --stale reaches a path the main tree did not move: a copy put back by hand to an older version, which the agent's
    /// fold would otherwise write into the main tree as its change, is handed the main tree's copy - and run again, the
    /// same line, finding the copy now the main tree's, is let stand.
    /// </summary>
    [Fact]
    public async Task AStaleNamingAPathTheMainTreeDidNotMove_HandsItTheMainTreesCopy_AndRunAgainIsLetStand()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nsecond\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the second version", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "one\n");

        var unnamed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: true, Token);

        Assert.True(unnamed.Succeeded, OrchestrationKit.Describe(unnamed));
        Assert.Equal("one\n", OrchestrationKit.Read(worktree, "a.txt"));

        var applied = await CliRunner.RunAsync(["refresh-agent", "o1", "ag", "a.txt", "--stale", "a.txt", "--apply"], Token, workingDirectory: kit.Main);

        Assert.Equal(0, applied.ExitCode);
        Assert.Equal("one\nsecond\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal(await DigestAsync(kit.Main, "a.txt"), kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths["a.txt"]);

        var again = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { Paths = ["a.txt"], Stale = ["a.txt"] }, apply: true, Token);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Equal("agent 'ag' of 'o1' holds the main tree's copy of every changed path under a.txt", again.Message);
    }

    /// <summary>
    /// The search of the main tree's history reads a bounded number of the commits that changed the paths, and says where
    /// it stopped short of that history: a copy older than it reached is not found stale.
    /// </summary>
    [Fact]
    public async Task TheSearchForStaleCopies_SaysWhereItStoppedShortOfTheMainTreesHistory()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nsecond\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the second version", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "one\n");
        await CommitManyAsync(kit.Main, "a.txt", TwoChanges.MostCommits + 1);

        var refused = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith("  a.txt   (a three-way merge of the two", refused.Details![0], StringComparison.Ordinal);
        Assert.DoesNotContain("its bytes are the main tree's", refused.Details[0], StringComparison.Ordinal);
        Assert.Contains(
            "The last 512 commit(s) of the main tree that changed these paths were searched, and its history goes further back: a copy older than that "
            + "is not found stale.",
            refused.Details);
        Assert.Equal("one\n", OrchestrationKit.Read(worktree, "a.txt"));
    }

    /// <summary>
    /// Commits <paramref name="count"/> versions of <paramref name="path"/> onto the main tree's branch at once, through
    /// git's own importer, and puts the tree's files as its HEAD then holds them.
    /// </summary>
    private static async Task CommitManyAsync(string main, string path, int count)
    {
        var harness = new HarnessFactory();
        var head = (await harness.RunGitAsync(main, ["rev-parse", "HEAD"], Token)).StandardOutput.Trim();
        var branch = (await harness.RunGitAsync(main, ["symbolic-ref", "HEAD"], Token)).StandardOutput.Trim();
        var stream = new System.Text.StringBuilder();

        for (var at = 1; at <= count; at++)
        {
            var content = $"version {at}\n";
            stream.Append($"commit {branch}\ncommitter Test <test@example.com> {1_700_000_000 + at} +0000\ndata 1\nv\n");

            if (at == 1)
            {
                stream.Append($"from {head}\n");
            }

            stream.Append($"M 100644 inline {path}\ndata {content.Length}\n{content}\n");
        }

        var import = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };

        foreach (var argument in new[] { "-C", main, "fast-import", "--quiet" })
        {
            import.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(import)!;
        await process.StandardInput.WriteAsync(stream.ToString());
        process.StandardInput.Close();
        var error = await process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);

        Assert.True(process.ExitCode == 0, $"git fast-import failed: {error}");
        await harness.RunGitAsync(main, ["reset", "--hard", "--quiet"], Token);
    }

    /// <summary>
    /// A path left as the agent changed it is handed nothing, and is in the way of what is handed like anything else of
    /// the agent's: where the main tree now holds a directory at it, the refresh is refused, naming it, and nothing is
    /// written - leaving it out by name never lets a file of the main tree's be written through it.
    /// </summary>
    [Fact]
    public async Task APathLeftAsTheAgentChangedIt_IsStillInTheWayOfWhatIsHanded_WhereTheMainTreeHoldsADirectoryThere()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        File.Delete(Path.Combine(kit.Main, "b.txt"));
        OrchestrationKit.Write(kit.Main, "b.txt/inner.txt", "inner\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a directory where a file was", Token);

        var refused = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true, Except = ["b.txt"] }, apply: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            "Refreshing agent 'ag' of 'o1' would write over or through 1 path(s) of its own - b.txt - where the main tree holds a directory, or a "
            + "file in place of the directory they are in: move them aside, and run again. Nothing was written.",
            refused.Message);
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(worktree, "b.txt"));
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
    /// A dry run of a refresh exits exactly as its --apply, typed with the same arguments, does: 13 where the agent's own
    /// changes refuse it, 0 once they are left with --except and the hand-over goes through.
    /// </summary>
    [Fact]
    public async Task ADryRunOfARefresh_ExitsExactlyAsItsApplyWould()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "one\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmain edit\n");

        var refusedDry = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true }, apply: false, Token);
        var refused = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true }, apply: true, Token);
        var handedDry = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true, Except = ["a.txt"] }, apply: false, Token);
        var handed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", new RefreshRequest { All = true, Except = ["a.txt"] }, apply: true, Token);

        Assert.Equal(HarnessExit.Refused, refusedDry.ExitCode);
        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.True(handedDry.Succeeded, OrchestrationKit.Describe(handedDry));
        Assert.True(handed.Succeeded, OrchestrationKit.Describe(handed));
        Assert.Equal("two\nmain edit\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal("one\nagent edit\n", OrchestrationKit.Read(worktree, "a.txt"));
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
    internal static async Task<string> DigestAsync(string root, string relative)
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

    /// <summary>The real file system, except that one file cannot be read, as while another program holds it.</summary>
    private sealed class UnreadableFileSystem(Core.FileSystem.IFileSystem inner, string held) : PassThroughFileSystem(inner)
    {
        public override Stream OpenRead(string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(held), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
            }

            return base.OpenRead(path);
        }
    }
}
