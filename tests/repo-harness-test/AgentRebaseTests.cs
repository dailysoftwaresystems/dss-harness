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

    /// <summary>
    /// An agent that committed inside its worktree is refused, saying so, and its base and HEAD stay: the commit is no move of
    /// its base stopped part way, and taken for one its record would name the agent's own commit, whose work its next fold
    /// would then never see.
    /// </summary>
    [Fact]
    public async Task Rebasing_AnAgentThatCommittedInsideItsWorktree_IsRefused_AndItsBaseStays()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(worktree, "a.txt", "one\nagent edit\n");
        await kit.Harness.CommitAllAsync(worktree, "inside the agent", Token);
        var agentHead = await HeadAsync(worktree);
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var refused = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("a commit made inside it", refused.Message);
        Assert.Equal(from, kit.Record("ag").Base);
        Assert.Equal(agentHead, await HeadAsync(worktree));
        Assert.Equal("two\n", OrchestrationKit.Read(worktree, "b.txt"));
    }

    /// <summary>
    /// A move git stops once what the new base brings is written, before HEAD moves, is incomplete, saying to run it again;
    /// run again, it finishes, the paths already written held as the new base holds them.
    /// </summary>
    [Fact]
    public async Task AMoveGitStopsPartWay_IsIncomplete_SayingToRunItAgain_AndRunningAgainFinishesIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var to = await HeadAsync(kit.Main);
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            ResetFailure = new HarnessException(HarnessExit.CommandFailed, "fatal: Unable to create 'index.lock': File exists."),
        };

        var stopped = await kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git).RebaseAsync(kit.Main, "o1", "ag", [], apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Equal(
            $"Moving the base of agent 'ag' of 'o1' from {ReportText.Commit(from)} to the main tree's HEAD {ReportText.Commit(to)} stopped part way: "
            + "fatal: Unable to create 'index.lock': File exists. Its worktree may hold part of what the new base brings; run "
            + "'dssharness rebase-agent o1 ag --apply' again once that is dealt with, and it finishes it.",
            stopped.Message);
        Assert.Equal(from, kit.Record("ag").Base);
        Assert.Equal(from, await HeadAsync(worktree));
        Assert.Equal("two\ncommitted\n", OrchestrationKit.Read(worktree, "b.txt"));

        var finished = await RebaseAsync(kit, apply: true);

        Assert.True(finished.Succeeded, OrchestrationKit.Describe(finished));
        Assert.Contains("and 1 path(s) it holds as the new base does already, with nothing to write:", finished.Details!);
        Assert.Equal(to, kit.Record("ag").Base);
        Assert.Equal(to, await HeadAsync(worktree));
    }

    /// <summary>
    /// A move is measured again once the agent's worktree is held: an edit the agent made to a path the new base brings,
    /// after the first measuring and before the hold, refuses it, and is never written over.
    /// </summary>
    [Fact]
    public async Task ARebase_MeasuresAgainUnderItsHold_SoAnEditMadeMeanwhileRefusesIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var measured = 0;
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            // Each measuring starts by asking what the two commits hold differently: the second is the one under the hold.
            BeforeListNames = arguments =>
            {
                if (arguments[0] == "diff" && ++measured == 2)
                {
                    OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit meanwhile\n");
                }
            },
        };

        var refused = await kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git).RebaseAsync(kit.Main, "o1", "ag", [], apply: true, Token);

        Assert.Equal(2, measured);
        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains($"  'b.txt': the agent changed it, and the main tree committed a change to it since the agent's base {ReportText.Commit(from)}", refused.Details!);
        Assert.Equal("two\nagent edit meanwhile\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal(from, kit.Record("ag").Base);
    }

    /// <summary>
    /// A move that stopped part way is finished where it was going, though the main tree has committed again since: it says
    /// it moved to the main tree's commit, that the main tree's HEAD is ahead of it now, and what moves its base there.
    /// </summary>
    [Fact]
    public async Task FinishingAMoveTheMainTreeHasMovedPast_SaysItsHeadIsAheadNow_AndHowToMoveThere()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var to = await HeadAsync(kit.Main);
        await kit.GitAsync(worktree, "checkout", to, "--", "b.txt");
        await kit.GitAsync(worktree, "reset", "--mixed", "--quiet", to);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\ncommitted later\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the next wave", Token);
        var head = await HeadAsync(kit.Main);

        var finished = await RebaseAsync(kit, apply: true);

        Assert.True(finished.Succeeded, OrchestrationKit.Describe(finished));
        Assert.Equal($"moved the base of agent 'ag' of 'o1' from {ReportText.Commit(from)} to the main tree's commit {ReportText.Commit(to)}", finished.Message);
        Assert.Contains(
            $"the main tree's HEAD is {ReportText.Commit(head)} now: run 'dssharness rebase-agent o1 ag --apply' again to move its base there",
            finished.Details!);
        Assert.Equal(to, kit.Record("ag").Base);
        Assert.Equal("one\n", OrchestrationKit.Read(worktree, "a.txt"));
    }

    /// <summary>
    /// Where git cannot say whether the main tree's history holds an agent's HEAD, the fold and the move each say it cannot
    /// be told - never that the agent committed inside its worktree, nor the reset that would undo such a commit, which
    /// for a move of its base stopped part way is the wrong remedy.
    /// </summary>
    [Fact]
    public async Task AnAgentHeadGitCannotPlace_IsSaidToBeOne_NeverGuessedAt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var to = await HeadAsync(kit.Main);
        await kit.GitAsync(worktree, "checkout", to, "--", "b.txt");
        await kit.GitAsync(worktree, "reset", "--mixed", "--quiet", to);
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            MergeBaseFailure = new HarnessException(HarnessExit.CommandFailed, "Could not find where they part: fatal: bad object."),
        };
        var agents = kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git);

        foreach (var outcome in new[]
        {
            await agents.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, Token),
            await agents.RebaseAsync(kit.Main, "o1", "ag", [], apply: true, Token),
        })
        {
            Assert.Equal(HarnessExit.CommandFailed, outcome.ExitCode);
            Assert.Equal(
                $"Agent 'ag' of 'o1''s HEAD is {ReportText.Commit(to)}, and its base is {ReportText.Commit(from)}, and whether that HEAD is a commit the "
                + "main tree's history holds - a move of its base that stopped part way - or one made inside it cannot be told: Could not find where they "
                + "part: fatal: bad object. Nothing was changed.",
                outcome.Message);
        }

        Assert.Equal(from, kit.Record("ag").Base);
    }

    /// <summary>
    /// A file the main tree committed inside a directory git will not look into in the agent's worktree - a repository of
    /// the agent's own - refuses the move: written there, it would land inside the agent's own work.
    /// </summary>
    [Fact]
    public async Task Rebasing_IsRefusedWhereTheMainTreeCommittedAFileInsideARepositoryOfTheAgentsOwn()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = ReportText.Commit(kit.Record("ag").Base!);
        Directory.CreateDirectory(Path.Combine(worktree, "vendor"));
        await kit.GitAsync(Path.Combine(worktree, "vendor"), "init", "--quiet");
        OrchestrationKit.Write(worktree, "vendor/own.txt", "the agent's own\n");
        OrchestrationKit.Write(kit.Main, "vendor/lib.txt", "the main tree's\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var refused = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains($"  'vendor/lib.txt': the agent made it, and the main tree committed a file there since the agent's base {from}", refused.Details!);
        Assert.False(File.Exists(Path.Combine(worktree, "vendor", "lib.txt")));
    }

    /// <summary>
    /// A file the agent made where the main tree has since committed one refuses the move, as the help says, whatever git
    /// makes of it: one git ignores, which no status lists and which the move would otherwise write over; one it staged; and
    /// one holding the very bytes the main tree committed, untracked or staged alike. Each is two makings of one path, for
    /// the agent's owner to settle by hand.
    /// </summary>
    [Fact]
    public async Task AFileTheAgentMadeWhereTheMainTreeCommittedOne_RefusesTheMove_IgnoredStagedOrTheSameBytes()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = ReportText.Commit(kit.Record("ag").Base!);

        // The evidence root is ignored, in both trees; the main tree commits a file there all the same.
        OrchestrationKit.Write(worktree, "evidence/local.json", "the agent's own\n");
        OrchestrationKit.Write(worktree, "same.txt", "both made it\n");
        OrchestrationKit.Write(worktree, "staged.txt", "both made it\n");
        await kit.GitAsync(worktree, "add", "staged.txt");
        OrchestrationKit.Write(kit.Main, "evidence/local.json", "the main tree's\n");
        OrchestrationKit.Write(kit.Main, "same.txt", "both made it\n");
        OrchestrationKit.Write(kit.Main, "staged.txt", "both made it\n");
        await kit.GitAsync(kit.Main, "add", "--force", "evidence/local.json");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var refused = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            [
                $"  'evidence/local.json': the agent made it, and the main tree committed a file there since the agent's base {from}",
                $"  'same.txt': the agent made it, and the main tree committed a file there since the agent's base {from}",
                $"  'staged.txt': the agent made it, and the main tree committed a file there since the agent's base {from}",
            ],
            refused.Details!.Take(3));
        Assert.Equal("the agent's own\n", OrchestrationKit.Read(worktree, "evidence/local.json"));
        Assert.Equal(kit.Record("ag").Base, await HeadAsync(worktree));
    }

    /// <summary>
    /// An agent's HEAD moved back by hand, to a commit before its base, is said to be one by the fold and by the move, each
    /// refusing: taken for a move of its base stopped part way, the move would have moved its base back.
    /// </summary>
    [Fact]
    public async Task AnAgentHeadMovedBackByHand_IsSaidToBeOne_NeverTakenForAMoveStoppedPartWay()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        await kit.GitAsync(worktree, "checkout", "--quiet", "--detach", "HEAD~1");
        var back = await HeadAsync(worktree);

        foreach (var outcome in new[] { await kit.FoldAsync("ag", apply: false), await RebaseAsync(kit, apply: true) })
        {
            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.StartsWith(
                $"Agent 'ag' of 'o1''s HEAD is {ReportText.Commit(back)}, a commit before its base {ReportText.Commit(from)}: it was moved back by hand",
                outcome.Message,
                StringComparison.Ordinal);
            Assert.Contains($"after a checkout, 'git -C \"{worktree}\" checkout {from}'", outcome.Message);
        }

        Assert.Equal(from, kit.Record("ag").Base);
        Assert.Equal(back, await HeadAsync(worktree));
    }

    /// <summary>
    /// A path the main tree committed since the agent's base whose name is not UTF-8 refuses the move, naming it: passed
    /// over, the agent's base would move while its worktree went without what that commit brought there.
    /// </summary>
    [Fact]
    public async Task APathTheMainTreeCommittedNamedOtherwiseThanInUtf8_RefusesTheMove_NamingIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base;
        await kit.Harness.StageAsync(kit.Main, "\"docs/caf\\351.md\"", "bytes\n", Token);
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "a name no file here can hold");
        await kit.Harness.SkipWorktreeAsync(kit.Main, "\"docs/caf\\351.md\"", Token);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => RebaseAsync(kit, apply: true));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(@"'docs/caf\351.md' is not named in UTF-8", refused.Message, StringComparison.Ordinal);
        Assert.Contains("the agent's base cannot be moved", refused.Message, StringComparison.Ordinal);
        Assert.Equal(from, kit.Record("ag").Base);
    }

    /// <summary>
    /// A name the agent's worktree holds that is not UTF-8 is never taken for the file its text spells: such a name reads with
    /// U+FFFD where its odd byte was, which a real file's name may hold, and that file - one the main tree committed a change
    /// to, and the agent never touched - comes in as any other does.
    /// </summary>
    [Fact]
    public async Task ANameNotInUtf8_IsNeverTakenForTheFileItsTextSpells()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "docs/caf�.md", "one\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a name holding U+FFFD", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        await kit.Harness.StageAsync(worktree, "\"docs/caf\\351.md\"", "bytes\n", Token);
        await kit.Harness.SkipWorktreeAsync(worktree, "\"docs/caf\\351.md\"", Token);
        OrchestrationKit.Write(kit.Main, "docs/caf�.md", "two\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var applied = await RebaseAsync(kit, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("two\n", OrchestrationKit.Read(worktree, "docs/caf�.md"));
    }

    private static Task<CommandOutcome> RebaseAsync(OrchestrationKit kit, bool apply, params string[] settled)
        => kit.Harness.AgentService.RebaseAsync(kit.Main, "o1", "ag", settled, apply, Token);

    private static async Task<string> HeadAsync(string tree) => (await new HarnessFactory().GitClient.ResolveCommitAsync(tree, "HEAD", Token))!;
}
