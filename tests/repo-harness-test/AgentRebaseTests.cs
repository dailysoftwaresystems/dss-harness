using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>rebase-agent: an agent's base moved to the main tree's HEAD, with its own work and what it was handed kept.</summary>
public sealed class AgentRebaseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Moving an agent's base brings what the main tree committed since into its worktree as git holds it - a change, a new
    /// file and a deletion - keeps the agent's own change, and leaves it standing on the main tree's HEAD: its fold then
    /// weighs only its own work. A dry run moves nothing.
    /// </summary>
    [Fact]
    public async Task Rebasing_BringsWhatTheMainTreeCommitted_AndKeepsTheAgentsOwnWork()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(worktree, "a.txt", "one\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        OrchestrationKit.Write(kit.Main, "docs/new.md", "new\n");
        File.Delete(Path.Combine(kit.Main, "docs", "x.md"));
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var to = await HeadAsync(kit.Main);

        var dry = await RebaseAsync(kit, apply: false);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Equal($"dry run: the base of agent 'ag' of 'o1' would move from {ReportText.Commit(from)} to the main tree's HEAD {ReportText.Commit(to)}; pass --apply to move it", dry.Message);
        Assert.Equal(["3 path(s) the main tree committed since come into its worktree as git holds them:", "  b.txt", "  docs/new.md", "  docs/x.md"], dry.Details);
        Assert.Equal("two\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal(from, await HeadAsync(worktree));

        var applied = await RebaseAsync(kit, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal($"moved the base of agent 'ag' of 'o1' from {ReportText.Commit(from)} to the main tree's HEAD {ReportText.Commit(to)}", applied.Message);
        Assert.Contains($"record {kit.Layout.AgentRecordFile("ag")}", applied.Details!);
        Assert.Contains($"log {kit.Layout.LogFile("ag")}", applied.Details!);
        Assert.Equal(to, kit.Record("ag").Base);
        Assert.Equal(to, await HeadAsync(worktree));
        Assert.Equal(to, await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, WorktreeService.BaseCommitRefPrefix + "o1/ag", Token));
        Assert.Equal("two\ncommitted\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal("new\n", OrchestrationKit.Read(worktree, "docs/new.md"));
        Assert.False(File.Exists(Path.Combine(worktree, "docs", "x.md")));
        Assert.Equal("one\nagent edit\n", OrchestrationKit.Read(worktree, "a.txt"));

        var fold = await kit.FoldAsync("ag", apply: true);

        Assert.True(fold.Succeeded, OrchestrationKit.Describe(fold));
        Assert.Contains("0 inherited path(s) left out; 1 path(s) are its own:", fold.Details!);
        Assert.Equal("one\nagent edit\n", OrchestrationKit.Read(kit.Main, "a.txt"));
        Assert.Equal("two\ncommitted\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// A path the agent changed that the main tree committed a change to since its base refuses the move, naming it, and
    /// nothing is changed; declared settled by hand, the agent's copy stays as its own change on the new base.
    /// </summary>
    [Fact]
    public async Task Rebasing_IsRefusedOverAPathBothChanged_UnlessSettled()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var refused = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("cannot move", refused.Message);
        Assert.Contains($"  'b.txt': the agent changed it, and the main tree committed a change to it since the agent's base {ReportText.Commit(from)}", refused.Details!);
        Assert.Contains(refused.Details!, line => line.Contains("--settled <path>", StringComparison.Ordinal));
        Assert.Equal(from, kit.Record("ag").Base);
        Assert.Equal(from, await HeadAsync(worktree));
        Assert.Equal("one\n", OrchestrationKit.Read(worktree, "a.txt"));

        var settled = await RebaseAsync(kit, apply: true, "b.txt");

        Assert.True(settled.Succeeded, OrchestrationKit.Describe(settled));
        Assert.Equal("1 path(s) it changed, declared settled by hand, kept as its own change on the new base:", settled.Details![0]);
        Assert.Equal("  b.txt", settled.Details[1]);
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal("one\ncommitted\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Contains("0 inherited path(s) left out; 1 path(s) are its own:", (await kit.FoldAsync("ag", apply: false)).Details!);
    }

    /// <summary>
    /// A file the agent made where the main tree committed one, and a file the agent deleted that the main tree changed, each
    /// refuse the move, saying which; a --settled naming a path no such clash holds settles nothing, and refuses too.
    /// </summary>
    [Fact]
    public async Task Rebasing_IsRefusedOverAFileBothMade_AndADeletionOfAChangedFile_AndAStraySettled()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = ReportText.Commit(kit.Record("ag").Base!);
        OrchestrationKit.Write(worktree, "n.txt", "the agent's\n");
        File.Delete(Path.Combine(worktree, "b.txt"));
        OrchestrationKit.Write(kit.Main, "n.txt", "the main tree's\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var refused = await RebaseAsync(kit, apply: false, "a.txt");

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            [
                "  --settled 'a.txt' names no path the agent changed that the main tree committed a change to since, so it settles nothing: check its spelling",
                $"  the agent deleted 'b.txt', and the main tree committed a change to it since the agent's base {from}",
                $"  'n.txt': the agent made it, and the main tree committed a file there since the agent's base {from}",
            ],
            refused.Details!.Take(3));
        Assert.Equal("the agent's\n", OrchestrationKit.Read(worktree, "n.txt"));
    }

    /// <summary>
    /// What the agent was handed stays as its seed records it when its base moves - here a copy the main tree then committed
    /// changed again - so its fold leaves it out, and refresh-agent hands it what the main tree holds now.
    /// </summary>
    [Fact]
    public async Task Rebasing_KeepsWhatTheAgentWasHanded_ForRefreshToHandAgain()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nhanded\n");
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nhanded\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var applied = await RebaseAsync(kit, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains("and 1 path(s) it shares with the main tree, handed to it or folded, stayed as its seed records them; refresh-agent hands it any the main tree moved since:", applied.Details!);
        Assert.Equal("x\nhanded\n", OrchestrationKit.Read(worktree, "docs/x.md"));
        Assert.Contains("1 inherited path(s) left out; 0 path(s) are its own:", (await kit.FoldAsync("ag", apply: false)).Details!);

        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", ["docs"], apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Equal("x\nhanded\ncommitted\n", OrchestrationKit.Read(worktree, "docs/x.md"));
        Assert.DoesNotContain(refreshed.Details!, line => line.StartsWith("its base ", StringComparison.Ordinal));
    }

    /// <summary>
    /// A move that stopped once the agent's worktree stood on the new base, before its record named it, is said to be one
    /// by the fold, which refuses it, and running rebase-agent again finishes it.
    /// </summary>
    [Fact]
    public async Task AMoveThatStoppedPartWay_IsSaidToBeOne_AndRunningAgainFinishesIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var to = await HeadAsync(kit.Main);
        await kit.GitAsync(worktree, "checkout", to, "--", "b.txt");
        await kit.GitAsync(worktree, "reset", "--mixed", "--quiet", to);

        var fold = await kit.FoldAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, fold.ExitCode);
        Assert.Contains("a commit the main tree's history holds", fold.Message);
        Assert.Contains("'dssharness rebase-agent o1 ag --apply' finishes it", fold.Message);

        var finished = await RebaseAsync(kit, apply: true);

        Assert.True(finished.Succeeded, OrchestrationKit.Describe(finished));
        Assert.Contains("and 1 path(s) it holds as the new base does already, with nothing to write:", finished.Details!);
        Assert.Equal(to, kit.Record("ag").Base);
        Assert.True((await kit.FoldAsync("ag", apply: false)).Succeeded);
    }

    /// <summary>A move whose record in git cannot be written still stands, its record naming the new base, and says so.</summary>
    [Fact]
    public async Task ABaseRefThatCannotBeWritten_IsSaid_AndTheMoveStands()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            InsteadOfRun = arguments => arguments[0] == "update-ref" ? new Core.Git.GitCommandResult(1, string.Empty, "fatal: cannot lock ref") : null,
        };

        var applied = await kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git).RebaseAsync(kit.Main, "o1", "ag", [], apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains("its base is recorded in its record, and could not be in git's record of the worktree: fatal: cannot lock ref", applied.Details!);
        Assert.Equal(await HeadAsync(kit.Main), kit.Record("ag").Base);
    }

    /// <summary>An agent whose base is the main tree's HEAD already is said to be, and nothing is written.</summary>
    [Fact]
    public async Task AnAgentOnTheMainTreesHead_IsSaidToBe()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");

        var already = await RebaseAsync(kit, apply: true);

        Assert.True(already.Succeeded, OrchestrationKit.Describe(already));
        Assert.Equal($"the base of agent 'ag' of 'o1' is the main tree's HEAD {ReportText.Commit(kit.Record("ag").Base!)} already", already.Message);
    }

    /// <summary>A file the main tree turned into a directory comes in as git holds it: the file goes, and what the directory holds is written.</summary>
    [Fact]
    public async Task AFileTheMainTreeTurnedIntoADirectory_ComesInAsGitHoldsIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        File.Delete(Path.Combine(kit.Main, "a.txt"));
        OrchestrationKit.Write(kit.Main, "a.txt/inner.txt", "inner\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var applied = await RebaseAsync(kit, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("inner\n", OrchestrationKit.Read(worktree, "a.txt/inner.txt"));
    }

    /// <summary>
    /// A symbolic link the main tree committed is named by refresh-agent and never handed as the file it leads to; moving the
    /// agent's base brings it in as git holds it.
    /// </summary>
    [Fact]
    public async Task ALinkTheMainTreeCommitted_IsNamedByARefresh_AndBroughtInByARebase()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        await kit.GitAsync(kit.Main, "config", "core.symlinks", "true");
        TestLinks.OrSkip(() => File.CreateSymbolicLink(Path.Combine(kit.Main, "docs", "link.md"), "x.md"));
        await kit.Harness.CommitAllAsync(kit.Main, "a link", Token);

        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", ["docs"], apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Contains(refreshed.Details!, line => line.StartsWith("not handed: docs/link.md - a symbolic link the main tree committed", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(worktree, "docs", "link.md")));

        await kit.GitAsync(worktree, "config", "core.symlinks", "true");
        var applied = await RebaseAsync(kit, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("x.md", new FileInfo(Path.Combine(worktree, "docs", "link.md")).LinkTarget);
    }

    private static Task<CommandOutcome> RebaseAsync(OrchestrationKit kit, bool apply, params string[] settled)
        => kit.Harness.AgentService.RebaseAsync(kit.Main, "o1", "ag", settled, apply, Token);

    private static async Task<string> HeadAsync(string tree) => (await new HarnessFactory().GitClient.ResolveCommitAsync(tree, "HEAD", Token))!;
}
