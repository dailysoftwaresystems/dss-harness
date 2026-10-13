using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Output;
using RepoHarness.Core.Repository;
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
        Assert.Contains(
            $"  'b.txt': the agent changed it, and the main tree committed a change to it since the agent's base {ReportText.Commit(from)}; a three-way merge of "
            + "the two conflicts in 1 place(s), shown below: reconcile it by hand, then --settled b.txt",
            refused.Details!);
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

        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Equal("x\nhanded\ncommitted\n", OrchestrationKit.Read(worktree, "docs/x.md"));
        Assert.DoesNotContain(refreshed.Details!, line => line.StartsWith("its base ", StringComparison.Ordinal));
    }

    /// <summary>
    /// A move that stopped once the agent's worktree stood on the new base, before its record named it, is said to be one
    /// by the fold, which refuses it, and running rebase-agent again finishes it - a file the new base added, which the move
    /// wrote, held as the new base holds it, never taken for one the agent made.
    /// </summary>
    [Fact]
    public async Task AMoveThatStoppedPartWay_IsSaidToBeOne_AndRunningAgainFinishesIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        OrchestrationKit.Write(kit.Main, "docs/added.md", "added\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var to = await HeadAsync(kit.Main);

        // Where a move stops once HEAD and the index stand on the new base, and before its record names it.
        kit.Harness.OrchestrationStore.UpdateAgent(kit.Layout, "ag", record => record with { Moving = to });
        await kit.GitAsync(worktree, "checkout", to, "--", "b.txt", "docs/added.md");
        await kit.GitAsync(worktree, "reset", "--mixed", "--quiet", to);

        var fold = await kit.FoldAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, fold.ExitCode);
        Assert.Equal(
            $"Moving the base of agent 'ag' of 'o1' from {ReportText.Commit(from)} to {ReportText.Commit(to)} stopped part way, and its worktree may hold "
            + "part of what the new base brings: 'dssharness rebase-agent o1 ag --apply' finishes it.",
            fold.Message);

        var finished = await RebaseAsync(kit, apply: true);

        Assert.True(finished.Succeeded, OrchestrationKit.Describe(finished));
        Assert.Contains("and 2 path(s) it holds as the new base does already, with nothing to write:", finished.Details!);
        Assert.Contains("  docs/added.md", finished.Details!);
        Assert.Equal(to, kit.Record("ag").Base);
        Assert.Null(kit.Record("ag").Moving);
        Assert.Contains("0 inherited path(s) left out; 0 path(s) are its own:", (await kit.FoldAsync("ag", apply: false)).Details!);
    }

    /// <summary>
    /// An agent's HEAD moved by hand to a later commit of the main tree's, with no move of its base under way, is said to be
    /// one by the fold and by the move, each refusing: never taken for a move stopped part way, which would move its base to
    /// wherever a hand put its HEAD.
    /// </summary>
    [Fact]
    public async Task AnAgentHeadMovedForwardByHand_IsSaidToBeOne_NeverFinishedAsAMove()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var to = await HeadAsync(kit.Main);
        await kit.GitAsync(worktree, "checkout", "--quiet", "--detach", to);

        foreach (var outcome in new[] { await kit.FoldAsync("ag", apply: false), await RebaseAsync(kit, apply: true) })
        {
            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.StartsWith(
                $"Agent 'ag' of 'o1''s HEAD is {ReportText.Commit(to)}, a commit of the main tree's after its base {ReportText.Commit(from)}, though no move "
                + "of its base is under way: it was moved there by hand",
                outcome.Message,
                StringComparison.Ordinal);
            Assert.Contains($"after a checkout, 'git -C \"{worktree}\" checkout {from}'", outcome.Message);
        }

        Assert.Equal(from, kit.Record("ag").Base);
        Assert.Equal(to, await HeadAsync(worktree));
    }

    /// <summary>
    /// A move stopped part way whose agent's HEAD a hand then moved to another commit of the main tree's - neither its base
    /// nor where the move goes - is never finished there, nor where the move goes: each refuses it, saying a move stands
    /// stopped, and once its HEAD is put back the move is finished where it was going.
    /// </summary>
    [Fact]
    public async Task AMoveStoppedPartWay_WhoseHeadAHandMovedElsewhere_IsNeverFinishedThere()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var between = await HeadAsync(kit.Main);
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted again\n");
        await kit.Harness.CommitAllAsync(kit.Main, "and again", Token);
        var to = await HeadAsync(kit.Main);
        kit.Harness.OrchestrationStore.UpdateAgent(kit.Layout, "ag", record => record with { Moving = to });
        await kit.GitAsync(worktree, "checkout", "--quiet", "--detach", between);

        foreach (var outcome in new[] { await kit.FoldAsync("ag", apply: false), await RebaseAsync(kit, apply: true) })
        {
            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.StartsWith(
                $"Agent 'ag' of 'o1''s HEAD is {ReportText.Commit(between)}, a commit of the main tree's after its base {ReportText.Commit(from)}, while a "
                + $"move of its base to {ReportText.Commit(to)} stands stopped part way: it was moved there by hand",
                outcome.Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(from, kit.Record("ag").Base);
        Assert.Equal(between, await HeadAsync(worktree));

        await kit.GitAsync(worktree, "checkout", "--quiet", from);
        var finished = await RebaseAsync(kit, apply: true);

        Assert.True(finished.Succeeded, OrchestrationKit.Describe(finished));
        Assert.Equal(to, kit.Record("ag").Base);
        Assert.Null(kit.Record("ag").Moving);
        Assert.Equal("two\ncommitted again\n", OrchestrationKit.Read(worktree, "b.txt"));
    }

    /// <summary>
    /// An agent's HEAD moved to a commit the main tree's history holds that is neither before its base nor after it - one
    /// of a line merged since - is said to have been moved there by hand, never taken for a move stopped part way.
    /// </summary>
    [Fact]
    public async Task AnAgentHeadOnACommitBesideItsBase_IsSaidToHaveBeenMovedByHand()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        await kit.GitAsync(kit.Main, "checkout", "--quiet", "-b", "beside", $"{from}~1");
        OrchestrationKit.Write(kit.Main, "beside.txt", "beside\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a line beside the agent's base", Token);
        var beside = await HeadAsync(kit.Main);
        await kit.GitAsync(kit.Main, "checkout", "--quiet", "-");
        await kit.GitAsync(kit.Main, "merge", "--quiet", "--no-edit", "beside");
        await kit.GitAsync(worktree, "checkout", "--quiet", "--detach", beside);

        foreach (var outcome in new[] { await kit.FoldAsync("ag", apply: false), await RebaseAsync(kit, apply: true) })
        {
            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.StartsWith(
                $"Agent 'ag' of 'o1''s HEAD is {ReportText.Commit(beside)}, a commit neither before nor after its base {ReportText.Commit(from)}: it was "
                + "moved there by hand",
                outcome.Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(from, kit.Record("ag").Base);
    }

    /// <summary>
    /// An agent whose HEAD names no commit - a branch with none yet checked out in it - is said to be one, with what puts its
    /// HEAD back, never taken for a commit made inside it.
    /// </summary>
    [Fact]
    public async Task AnAgentHeadNamingNoCommit_IsSaidToBeOne()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        await kit.GitAsync(worktree, "checkout", "--quiet", "--orphan", "nowhere");

        foreach (var outcome in new[] { await kit.FoldAsync("ag", apply: false), await RebaseAsync(kit, apply: true) })
        {
            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.Equal(
                $"Agent 'ag' of 'o1''s HEAD names no commit - a branch with none yet was checked out in it - so its work cannot be weighed against its "
                + $"base {ReportText.Commit(from)}: 'git -C \"{worktree}\" reset --soft {from}' puts its HEAD back on its base, its files as they are; "
                + "then run again.",
                outcome.Message);
        }

        Assert.Equal(from, kit.Record("ag").Base);
    }

    /// <summary>
    /// While a move of an agent's base stands stopped part way, every command that works with its worktree but rebase-agent
    /// refuses it, saying so - deleting, seeding and refreshing as folding do - and so does each one an agent whose HEAD was
    /// moved off its base; rebase-agent finishes the move, and the agent is worked with again.
    /// </summary>
    [Fact]
    public async Task AMoveStoppedPartWay_OrAHeadOffItsBase_RefusesEveryCommandButTheMove()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            ResetFailure = new HarnessException(HarnessExit.CommandFailed, "fatal: Unable to create 'index.lock': File exists."),
        };
        var stopped = await kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git).RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token);
        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);

        foreach (var outcome in await EveryCommandButTheMoveAsync(kit))
        {
            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.Contains("stopped part way, and its worktree may hold part of what the new base brings: 'dssharness rebase-agent o1 ag --apply' finishes it.", outcome.Message);
        }

        Assert.True((await RebaseAsync(kit, apply: true)).Succeeded);
        OrchestrationKit.Write(worktree, "a.txt", "one\nagent edit\n");
        await kit.Harness.CommitAllAsync(worktree, "inside the agent", Token);

        foreach (var outcome in await EveryCommandButTheMoveAsync(kit))
        {
            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.Contains("a commit made inside it", outcome.Message);
        }

        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
        Assert.True(Directory.Exists(worktree));
    }

    /// <summary>
    /// A file of the agent's own where the main tree has since committed a directory - a file it made, git ignores or not -
    /// refuses the move, naming it: git, writing what the directory holds, would remove the file without a word.
    /// </summary>
    [Fact]
    public async Task AFileTheAgentMadeWhereTheMainTreeCommittedADirectory_RefusesTheMove_AndIsKept()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = ReportText.Commit(kit.Record("ag").Base!);
        OrchestrationKit.Write(worktree, "notes/design", "the agent's own design\n");
        OrchestrationKit.Write(kit.Main, "notes/design/overview.md", "the main tree's\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var refused = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            $"  'notes/design': the agent made it, and the main tree committed 'notes/design/overview.md' under it since the agent's base {from}, which needs "
            + "a directory there: move it aside, and run again",
            refused.Details!);
        Assert.Equal("the agent's own design\n", OrchestrationKit.Read(worktree, "notes/design"));
        Assert.Equal(kit.Record("ag").Base, await HeadAsync(worktree));
    }

    /// <summary>
    /// A directory the main tree turned into a file comes in as git holds it, the directory's files gone with it, and the
    /// agent's fold then has nothing to remove; where the agent holds a file of its own in that directory, the move is
    /// refused, naming it, and the file is kept: git, writing the file, would remove the directory with all it holds.
    /// </summary>
    [Fact]
    public async Task ADirectoryTheMainTreeTurnedIntoAFile_ComesIn_UnlessTheAgentHoldsAFileOfItsOwnThere()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = ReportText.Commit(kit.Record("ag").Base!);
        OrchestrationKit.Write(worktree, "docs/mine.md", "the agent's own\n");
        Directory.Delete(Path.Combine(kit.Main, "docs"), recursive: true);
        OrchestrationKit.Write(kit.Main, "docs", "a file now\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var refused = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            $"  'docs': the main tree committed a file there since the agent's base {from}, where the agent's worktree holds a directory with files of its "
            + "own - docs/mine.md - that writing it would remove: move them aside, and run again",
            refused.Details!);
        Assert.Equal("the agent's own\n", OrchestrationKit.Read(worktree, "docs/mine.md"));

        File.Delete(Path.Combine(worktree, "docs", "mine.md"));
        var applied = await RebaseAsync(kit, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal(["2 path(s) the main tree committed since came into its worktree as git holds them:", "  docs", "  docs/x.md"], applied.Details!.Take(3));
        Assert.Equal("a file now\n", OrchestrationKit.Read(worktree, "docs"));
        Assert.Contains("0 inherited path(s) left out; 0 path(s) are its own:", (await kit.FoldAsync("ag", apply: false)).Details!);
    }

    /// <summary>
    /// A directory the agent and the main tree each turned into a file refuses the move, as any path both changed does, and
    /// the agent's file is kept: its base held a directory there, and a file is no directory the agent holds as it did.
    /// </summary>
    [Fact]
    public async Task ADirectoryTheAgentAndTheMainTreeEachTurnedIntoAFile_RefusesTheMove_AndTheAgentsIsKept()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = ReportText.Commit(kit.Record("ag").Base!);
        Directory.Delete(Path.Combine(worktree, "docs"), recursive: true);
        OrchestrationKit.Write(worktree, "docs", "the agent's\n");
        Directory.Delete(Path.Combine(kit.Main, "docs"), recursive: true);
        OrchestrationKit.Write(kit.Main, "docs", "the main tree's\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var refused = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal($"  'docs': the agent changed it, and the main tree committed a change to it since the agent's base {from}", refused.Details![0]);
        Assert.Equal("the agent's\n", OrchestrationKit.Read(worktree, "docs"));
        Assert.Equal(kit.Record("ag").Base, await HeadAsync(worktree));
    }

    /// <summary>
    /// What comes in over a path the agent shares with the main tree, or one it declared settled, refuses the move, each
    /// saying what to do - a file of either where the new base needs a directory, and either in a directory where it holds a
    /// file - and a path refused on its own line is never said again for what comes in under it.
    /// </summary>
    [Fact]
    public async Task WhatComesInWhereTheAgentSharesOrSettledAPath_RefusesTheMove_SayingForEachWhatToDo()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "lib/y.md", "y\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a directory", Token);
        OrchestrationKit.Write(kit.Main, "notes", "handed notes\n");
        OrchestrationKit.Write(kit.Main, "docs/handed.md", "handed\n");
        var worktree = await kit.CreateAgentAsync("ag");
        var from = ReportText.Commit(kit.Record("ag").Base!);
        File.Delete(Path.Combine(kit.Main, "notes"));
        OrchestrationKit.Write(kit.Main, "notes/a.md", "the main tree's\n");
        File.Delete(Path.Combine(kit.Main, "a.txt"));
        OrchestrationKit.Write(kit.Main, "a.txt/x", "the main tree's\n");
        File.Delete(Path.Combine(kit.Main, "b.txt"));
        OrchestrationKit.Write(kit.Main, "b.txt/y", "the main tree's\n");
        Directory.Delete(Path.Combine(kit.Main, "docs"), recursive: true);
        OrchestrationKit.Write(kit.Main, "docs", "a file now\n");
        Directory.Delete(Path.Combine(kit.Main, "lib"), recursive: true);
        OrchestrationKit.Write(kit.Main, "lib", "a file now\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        OrchestrationKit.Write(worktree, "a.txt", "one\nthe agent's\n");
        OrchestrationKit.Write(worktree, "b.txt", "two\nthe agent's\n");
        OrchestrationKit.Write(worktree, "lib/y.md", "y\nthe agent's\n");

        var refused = await RebaseAsync(kit, apply: true, "a.txt", "lib/y.md");

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            [
                $"  'a.txt' was declared settled, and the main tree committed 'a.txt/x' under it since the agent's base {from}, which needs a directory there, "
                    + "so it cannot stay as the agent's change: reconcile it by hand, and run again without --settled for it",
                $"  'b.txt': the agent changed it, and the main tree committed a change to it since the agent's base {from}",
                $"  'docs': the main tree committed a file there since the agent's base {from}, where the agent shares docs/handed.md with the main tree, which "
                    + "writing it would remove: refresh-agent hands it what the main tree holds there first",
                $"  'lib': the main tree committed a file there since the agent's base {from}, and writing it would remove lib/y.md, declared settled: "
                    + "reconcile them by hand, and run again without --settled for them",
                $"  'notes' is shared with the main tree - handed to the agent, or folded - and the main tree committed 'notes/a.md' under it since the "
                    + $"agent's base {from}, which needs a directory there: refresh-agent hands it what the main tree holds there first",
            ],
            refused.Details!.Take(5));
        Assert.Equal("one\nthe agent's\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal("handed\n", OrchestrationKit.Read(worktree, "docs/handed.md"));
        Assert.Equal(kit.Record("ag").Base, await HeadAsync(worktree));
    }

    /// <summary>
    /// A submodule the main tree moved to another commit comes in as git holds it: its entry is no directory the agent made,
    /// and the agent's fold then has nothing of it.
    /// </summary>
    [Fact]
    public async Task ASubmoduleTheMainTreeMoved_ComesIn()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var first = await HeadAsync(kit.Main);
        Directory.CreateDirectory(Path.Combine(kit.Main, "lib", "sub"));
        await kit.GitAsync(kit.Main, "update-index", "--add", "--cacheinfo", $"160000,{first},lib/sub");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "a submodule");
        var worktree = await kit.CreateAgentAsync("ag");
        await kit.GitAsync(kit.Main, "update-index", "--cacheinfo", $"160000,{kit.Record("ag").Base},lib/sub");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "the submodule moved");

        var applied = await RebaseAsync(kit, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal(["1 path(s) the main tree committed since came into its worktree as git holds them:", "  lib/sub"], applied.Details!.Take(2));
        Assert.True(Directory.Exists(Path.Combine(worktree, "lib", "sub")));
        Assert.True((await kit.FoldAsync("ag", apply: false)).Succeeded);
    }

    /// <summary>
    /// A submodule the main tree moved comes in as its entry alone where the agent's worktree holds its checkout: git moves
    /// the entry and leaves the checkout as it is, so nothing the checkout holds is taken for anything in the way.
    /// </summary>
    [Fact]
    public async Task ASubmoduleTheMainTreeMoved_ComesIn_ItsCheckoutInTheAgentLeftAsItIs()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var first = await HeadAsync(kit.Main);
        Directory.CreateDirectory(Path.Combine(kit.Main, "lib", "sub"));
        await kit.GitAsync(kit.Main, "update-index", "--add", "--cacheinfo", $"160000,{first},lib/sub");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "a submodule");
        var worktree = await kit.CreateAgentAsync("ag");
        var checkout = Path.Combine(worktree, "lib", "sub");
        await kit.GitAsync(worktree, "clone", "--quiet", "--no-checkout", kit.Main, checkout);
        await kit.GitAsync(checkout, "checkout", "--quiet", "--detach", first);
        await kit.GitAsync(kit.Main, "update-index", "--cacheinfo", $"160000,{kit.Record("ag").Base},lib/sub");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "the submodule moved");

        var applied = await RebaseAsync(kit, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal(["1 path(s) the main tree committed since came into its worktree as git holds them:", "  lib/sub"], applied.Details!.Take(2));
        Assert.True(File.Exists(Path.Combine(checkout, "a.txt")));
        Assert.Equal(first, await HeadAsync(checkout));
    }

    /// <summary>
    /// A directory the main tree turned into a submodule's entry comes in as git holds it - what the directory held gone, an
    /// empty directory for the entry - never left in place where the entry hides it from git; where the agent holds a file
    /// of its own in that directory, the move is refused, naming it, and the file is kept.
    /// </summary>
    [Fact]
    public async Task ADirectoryTheMainTreeTurnedIntoASubmodule_ComesIn_UnlessTheAgentHoldsAFileOfItsOwnThere()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(worktree, "docs/mine.md", "the agent's own\n");
        await kit.GitAsync(kit.Main, "rm", "-r", "--quiet", "docs");
        Directory.CreateDirectory(Path.Combine(kit.Main, "docs"));
        await kit.GitAsync(kit.Main, "update-index", "--add", "--cacheinfo", $"160000,{from},docs");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "a submodule where a directory was");

        var refused = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            $"  'docs': the main tree committed a submodule's entry there since the agent's base {ReportText.Commit(from)}, where the agent's worktree holds a "
            + "directory with files of its own - docs/mine.md - that writing it would hide from git: move them aside, and run again",
            refused.Details!);
        Assert.Equal("the agent's own\n", OrchestrationKit.Read(worktree, "docs/mine.md"));

        File.Delete(Path.Combine(worktree, "docs", "mine.md"));
        var applied = await RebaseAsync(kit, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal(["2 path(s) the main tree committed since came into its worktree as git holds them:", "  docs", "  docs/x.md"], applied.Details!.Take(3));
        Assert.True(Directory.Exists(Path.Combine(worktree, "docs")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(worktree, "docs")));
        Assert.Contains("0 inherited path(s) left out; 0 path(s) are its own:", (await kit.FoldAsync("ag", apply: false)).Details!);
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

        var applied = await kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git).RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token);

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

        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: true, Token);

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
    /// A move git stops once what the new base brings is written, before HEAD moves, is incomplete, saying to run it again,
    /// and its record says where it was going; run again, it finishes, the paths already written - a file the new base added
    /// among them - held as the new base holds them.
    /// </summary>
    [Fact]
    public async Task AMoveGitStopsPartWay_IsIncomplete_SayingToRunItAgain_AndRunningAgainFinishesIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        OrchestrationKit.Write(kit.Main, "docs/added.md", "added\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var to = await HeadAsync(kit.Main);
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            ResetFailure = new HarnessException(HarnessExit.CommandFailed, "fatal: Unable to create 'index.lock': File exists."),
        };

        var stopped = await kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git).RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Equal(
            $"Moving the base of agent 'ag' of 'o1' from {ReportText.Commit(from)} to the main tree's HEAD {ReportText.Commit(to)} stopped part way: "
            + "fatal: Unable to create 'index.lock': File exists. Its worktree may hold part of what the new base brings; run "
            + "'dssharness rebase-agent o1 ag --apply' again once that is dealt with, and it finishes it.",
            stopped.Message);
        Assert.Equal(from, kit.Record("ag").Base);
        Assert.Equal(to, kit.Record("ag").Moving);
        Assert.Equal(from, await HeadAsync(worktree));
        Assert.Equal("two\ncommitted\n", OrchestrationKit.Read(worktree, "b.txt"));

        var finished = await RebaseAsync(kit, apply: true);

        Assert.True(finished.Succeeded, OrchestrationKit.Describe(finished));
        Assert.Contains("and 2 path(s) it holds as the new base does already, with nothing to write:", finished.Details!);
        Assert.Contains("  docs/added.md", finished.Details!);
        Assert.Equal(to, kit.Record("ag").Base);
        Assert.Null(kit.Record("ag").Moving);
        Assert.Equal(to, await HeadAsync(worktree));
        Assert.Contains("0 inherited path(s) left out; 0 path(s) are its own:", (await kit.FoldAsync("ag", apply: false)).Details!);
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

        var refused = await kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git).RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token);

        Assert.Equal(2, measured);
        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            $"  'b.txt': the agent changed it, and the main tree committed a change to it since the agent's base {ReportText.Commit(from)}; a three-way merge of "
            + "the two conflicts in 1 place(s), shown below: reconcile it by hand, then --settled b.txt",
            refused.Details!);
        Assert.Equal("two\nagent edit meanwhile\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal(from, kit.Record("ag").Base);
    }

    /// <summary>
    /// A move that stopped part way - here before HEAD moved, its new base's files written - is finished where it was going,
    /// though the main tree has since committed a further change to one of them: what the move wrote is never taken for the
    /// agent's change. It says it moved to the main tree's commit, that the main tree's HEAD is ahead of it now, and what
    /// moves its base there.
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
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            ResetFailure = new HarnessException(HarnessExit.CommandFailed, "fatal: Unable to create 'index.lock': File exists."),
        };
        Assert.Equal(
            HarnessExit.Incomplete,
            (await kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git).RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token)).ExitCode);
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\ncommitted later\n");
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
        Assert.Equal("two\ncommitted\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal("one\n", OrchestrationKit.Read(worktree, "a.txt"));

        var again = await RebaseAsync(kit, apply: true);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Equal(head, kit.Record("ag").Base);
        Assert.Equal("two\ncommitted\ncommitted later\n", OrchestrationKit.Read(worktree, "b.txt"));
    }

    /// <summary>
    /// Where git cannot say how an agent's HEAD came off its base, the fold and the move each say it cannot be told - never
    /// that the agent committed inside its worktree, nor the reset that would undo such a commit, which for a HEAD moved by
    /// hand is the wrong remedy.
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
        await kit.GitAsync(worktree, "checkout", "--quiet", "--detach", to);
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            MergeBaseFailure = new HarnessException(HarnessExit.CommandFailed, "Could not find where they part: fatal: bad object."),
        };
        var agents = kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git);

        foreach (var outcome in new[]
        {
            await agents.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: false, Token),
            await agents.RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token),
        })
        {
            Assert.Equal(HarnessExit.CommandFailed, outcome.ExitCode);
            Assert.Equal(
                $"Agent 'ag' of 'o1''s HEAD is {ReportText.Commit(to)}, and its base is {ReportText.Commit(from)}, and how its HEAD came off its base - "
                + "moved by hand, or a commit made inside it - cannot be told: Could not find where they part: fatal: bad object. Nothing was changed.",
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
    /// A file the agent made where the main tree has since committed another refuses the move, as the help says, whatever
    /// git makes of it: one git ignores, which no status lists and which the move would otherwise write over; one it staged;
    /// and one a single byte off the file committed. Each is two makings of one path, for the agent's owner to settle by
    /// hand.
    /// </summary>
    [Fact]
    public async Task AFileTheAgentMadeWhereTheMainTreeCommittedAnother_RefusesTheMove_IgnoredStagedOrOneByteOff()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = ReportText.Commit(kit.Record("ag").Base!);

        // The evidence root is ignored, in both trees; the main tree commits a file there all the same.
        OrchestrationKit.Write(worktree, "evidence/local.json", "the agent's own\n");
        OrchestrationKit.Write(worktree, "near.txt", "both made it\n.");
        OrchestrationKit.Write(worktree, "staged.txt", "the agent's own\n");
        await kit.GitAsync(worktree, "add", "staged.txt");
        OrchestrationKit.Write(kit.Main, "evidence/local.json", "the main tree's\n");
        OrchestrationKit.Write(kit.Main, "near.txt", "both made it\n");
        OrchestrationKit.Write(kit.Main, "staged.txt", "the main tree's\n");
        await kit.GitAsync(kit.Main, "add", "--force", "evidence/local.json");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var refused = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            [
                $"  'evidence/local.json': the agent made it, and the main tree committed a file there since the agent's base {from}",
                $"  'near.txt': the agent made it, and the main tree committed a file there since the agent's base {from}",
                $"  'staged.txt': the agent made it, and the main tree committed a file there since the agent's base {from}",
            ],
            refused.Details!.Take(3));
        Assert.Equal("the agent's own\n", OrchestrationKit.Read(worktree, "evidence/local.json"));
        Assert.Equal(kit.Record("ag").Base, await HeadAsync(worktree));
    }

    /// <summary>
    /// A file the agent made that is the very file the main tree has since committed there - the same content as git
    /// compares it, untracked, staged, or one git ignores - is nothing to reconcile: the move says the agent holds it as the
    /// new base does already, writes nothing over it, and leaves it no change of the agent's. Naming it with --settled
    /// settles nothing, as for any path the agent holds as the new base does.
    /// </summary>
    [Fact]
    public async Task AFileTheAgentMadeThatIsTheFileTheMainTreeCommitted_IsHeldAsTheNewBaseHoldsIt_UntrackedStagedOrIgnored()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(worktree, "evidence/same.json", "both made it\n");
        OrchestrationKit.Write(worktree, "same.txt", "both made it\n");
        OrchestrationKit.Write(worktree, "staged.txt", "both made it\n");
        await kit.GitAsync(worktree, "add", "staged.txt");
        OrchestrationKit.Write(worktree, "a.txt", "one\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "evidence/same.json", "both made it\n");
        OrchestrationKit.Write(kit.Main, "same.txt", "both made it\n");
        OrchestrationKit.Write(kit.Main, "staged.txt", "both made it\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.GitAsync(kit.Main, "add", "--force", "evidence/same.json");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var to = await HeadAsync(kit.Main);

        var dry = await RebaseAsync(kit, apply: false);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Equal(
            [
                "1 path(s) the main tree committed since come into its worktree as git holds them:",
                "  b.txt",
                "and 3 path(s) it holds as the new base does already, with nothing to write:",
                "  evidence/same.json",
                "  same.txt",
                "  staged.txt",
            ],
            dry.Details);
        Assert.Equal(from, await HeadAsync(worktree));

        var stray = await RebaseAsync(kit, apply: true, "same.txt");

        Assert.Equal(HarnessExit.Refused, stray.ExitCode);
        Assert.Equal("  --settled 'same.txt' names no path the agent changed that the main tree committed a change to since, so it settles nothing: check its spelling", stray.Details![0]);
        Assert.Equal(from, await HeadAsync(worktree));

        var applied = await RebaseAsync(kit, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal(to, await HeadAsync(worktree));
        Assert.Equal(to, kit.Record("ag").Base);
        Assert.Equal("both made it\n", OrchestrationKit.Read(worktree, "same.txt"));
        Assert.Equal("both made it\n", OrchestrationKit.Read(worktree, "evidence/same.json"));
        Assert.Equal("two\ncommitted\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal(["a.txt"], (await kit.Harness.GitClient.ReadStatusAsync(worktree, Token)).Select(entry => entry.Path.Text));
        Assert.Contains("0 inherited path(s) left out; 1 path(s) are its own:", (await kit.FoldAsync("ag", apply: false)).Details!);
    }

    /// <summary>
    /// Where the repository trusts file modes, a file the agent made with the bytes the main tree committed there and
    /// another mode is not the file committed: the move is refused over it, as git would show it changed.
    /// </summary>
    [Fact]
    public async Task AFileTheAgentMadeWithTheBytesCommittedAndAnotherMode_RefusesTheMove_WhereModesAreTrusted()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit, so git trusts no mode there.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = ReportText.Commit(kit.Record("ag").Base!);
        OrchestrationKit.Write(worktree, "tool.sh", "#!/bin/sh\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(worktree, "tool.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        OrchestrationKit.Write(kit.Main, "tool.sh", "#!/bin/sh\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var refused = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal($"  'tool.sh': the agent made it, and the main tree committed a file there since the agent's base {from}", refused.Details![0]);
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

    /// <summary>
    /// Each way a move is refused before anything is written says why, and moves nothing: a --settled naming no path in the
    /// tree, a worktree another command holds, a path that cannot be looked at while it is measured, a main tree with no
    /// commit to move to, and an agent whose making stopped part way.
    /// </summary>
    [Fact]
    public async Task EachWayAMoveIsRefusedBeforeAnythingIsWritten_SaysWhy_AndMovesNothing()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(kit.Main, "docs/new.md", "new\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var usage = await RebaseAsync(kit, apply: true, "../x");

        Assert.Equal(HarnessExit.UsageError, usage.ExitCode);
        Assert.Equal("--settled, '../x', is not a path relative to the tree, spelt with forward slashes", usage.Message);

        var building = await kit.Harness.RunLock.TryAcquireAsync(
            new HarnessLayout(kit.Main, kit.Main),
            new LockRequest { Host = HostId.Local.ToString(), Tree = kit.Harness.FileSystem.ResolveLinks(worktree), Variant = "linux-x86_64-debug", Scope = LockScope.TreeShared, RunId = RunId.New(), Command = "build" },
            Token);

        await using (building.Handle)
        {
            var held = await RebaseAsync(kit, apply: true);

            Assert.Equal(HarnessExit.Refused, held.ExitCode);
            Assert.EndsWith("Nothing was changed; run it again once that is done.", held.Message, StringComparison.Ordinal);
        }

        var unreadable = new UnlookableFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "docs", "new.md"));
        var unread = await kit.Harness.Agents(unreadable, kit.Harness.AnchorRegistryService, kit.Harness.GitClient).RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token);

        Assert.Equal(HarnessExit.CommandFailed, unread.ExitCode);
        Assert.Equal(
            "The base of agent 'ag' of 'o1' was not moved: what it holds cannot be read - Access to the path is denied. Nothing was changed.",
            unread.Message);

        await kit.GitAsync(kit.Main, "checkout", "--quiet", "--orphan", "nothing-yet");
        var nothing = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, nothing.ExitCode);
        Assert.Equal("The main tree has no commit for the base of agent 'ag' of 'o1' to move to. Nothing was changed.", nothing.Message);

        File.Delete(kit.Layout.SeedFile("ag"));
        var unmade = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, unmade.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' is not whole - making it stopped part way: ", unmade.Message, StringComparison.Ordinal);
        Assert.Equal(from, kit.Record("ag").Base);
        Assert.Null(kit.Record("ag").Moving);
        Assert.Equal(from, await HeadAsync(worktree));
        Assert.False(File.Exists(Path.Combine(worktree, "docs", "new.md")));
    }

    /// <summary>The closing paragraph of a refused move: how a path both changed is let through.</summary>
    private const string BothChangedParagraph =
        "A path the agent changed that the main tree committed a change to since is two changes of one file. Where a three-way merge of the two "
        + "is clean, --merge <path> writes it into the agent's worktree as its own change on the new base: it is never taken unasked, and a clean "
        + "textual merge proves nothing about the program. Otherwise reconcile the agent's copy with what the main tree committed by hand, then "
        + "run again naming the path with --settled <path>, which keeps its copy as its own change on the new base. Neither is a --force.";

    /// <summary>The file the agent and the main tree both change.</summary>
    private const string Both = "src/m.txt";

    /// <summary>
    /// An agent made at a commit that holds <see cref="Both"/>, which it then changes as <paramref name="agent"/> says while
    /// the main tree commits it as <paramref name="main"/> says.
    /// </summary>
    private static async Task<(OrchestrationKit Kit, string Worktree, string From)> BothChangedAsync(TempDirectory temp, string agent, string main)
    {
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text());
        await kit.Harness.CommitAllAsync(kit.Main, "a file two will change", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, Both, agent);
        OrchestrationKit.Write(kit.Main, Both, main);
        await kit.Harness.CommitAllAsync(kit.Main, "the main tree's change", Token);

        return (kit, worktree, kit.Record("ag").Base!);
    }

    private static RebaseRequest Merging(params string[] paths) => new() { Merge = paths };

    /// <summary>
    /// A path the agent changed that the main tree committed a change to still refuses the move, dry run or not: its line
    /// says what a three-way merge of the two gives, and the last of the refusal's lines names the command that takes it.
    /// Measured and said, it is never taken unasked.
    /// </summary>
    [Fact]
    public async Task APathBothChanged_StillRefusesTheMove_AndItsLineSaysTheMergeIsClean_AndNamesTheCommandThatTakesIt()
    {
        using var temp = new TempDirectory();
        var (kit, worktree, from) = await BothChangedAsync(temp, AgentFoldMergeTests.Text((2, "AGENT 2")), AgentFoldMergeTests.Text((9, "MAIN 9")));

        foreach (var apply in new[] { false, true })
        {
            var refused = await RebaseAsync(kit, apply);

            Assert.Equal(HarnessExit.Refused, refused.ExitCode);
            Assert.Contains(
                $"  '{Both}': the agent changed it, and the main tree committed a change to it since the agent's base {ReportText.Commit(from)}; a three-way merge of "
                + $"the two is clean - 1 hunk(s) from the agent, 1 from the main tree - and --merge {Both} writes it",
                refused.Details!);
            Assert.Contains(BothChangedParagraph, refused.Details!);
            Assert.Contains($"to merge the 1 path(s) that merge clean: '{ToolPackage.Command} rebase-agent o1 ag --merge {Both}'", refused.Details!);
            Assert.Equal(AgentFoldMergeTests.Text((2, "AGENT 2")), OrchestrationKit.Read(worktree, Both));
            Assert.Equal(from, kit.Record("ag").Base);
            Assert.Equal(from, await HeadAsync(worktree));
        }
    }

    /// <summary>The line that names the command taking every clean merge keeps every path the move was told to merge or keep.</summary>
    [Fact]
    public async Task TheLineNamingEveryCleanMerge_KeepsEveryOptionTheMoveWasGiven()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        const string Other = "src/n.txt";
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text());
        OrchestrationKit.Write(kit.Main, Other, AgentFoldMergeTests.Text());
        await kit.Harness.CommitAllAsync(kit.Main, "two files two will change", Token);
        var worktree = await kit.CreateAgentAsync("ag");

        foreach (var path in new[] { Both, Other })
        {
            OrchestrationKit.Write(worktree, path, AgentFoldMergeTests.Text((2, "AGENT 2")));
            OrchestrationKit.Write(kit.Main, path, AgentFoldMergeTests.Text((9, "MAIN 9")));
        }

        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the main tree's changes", Token);

        var refused = await RebaseAsync(kit, apply: false, new RebaseRequest { Merge = [Both], Settled = ["b.txt"] });

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"to merge the 1 path(s) that merge clean: '{ToolPackage.Command} rebase-agent o1 ag --merge {Both} --merge {Other} --settled b.txt'",
            refused.Details![^1]);
    }

    /// <summary>
    /// Named with --merge, a clean merge is written into the agent's worktree as its own change on the new base, listed
    /// before what comes in, and nothing is recorded for it: its fold weighs it against the new base and writes it.
    /// </summary>
    [Fact]
    public async Task NamedToBeMerged_ItIsWrittenAsTheAgentsOwnChangeOnTheNewBase_AndNothingIsRecorded()
    {
        using var temp = new TempDirectory();
        var (kit, worktree, _) = await BothChangedAsync(
            temp,
            AgentFoldMergeTests.Text((2, "AGENT 2")),
            AgentFoldMergeTests.Text((9, "MAIN 9"), (11, "MAIN 11")));
        var to = await HeadAsync(kit.Main);
        var seeded = File.ReadAllText(kit.Layout.SeedFile("ag"));
        var merged = AgentFoldMergeTests.Text((2, "AGENT 2"), (9, "MAIN 9"), (11, "MAIN 11"));

        var dry = await RebaseAsync(kit, apply: false, Merging(Both));

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Equal(
            [
                "1 path(s) it changed that the main tree committed a change to, merged three-way, to write into its worktree as its own change on the "
                + "new base - a clean textual merge proves nothing about the program:",
                $"  {Both}   (--merge: 1 hunk(s) from the agent, 2 from the main tree)",
                "the agent's session must read these again before it writes them: a write made from what it held before would fold as its change, "
                + "over what the main tree brought",
                "0 path(s) the main tree committed since come into its worktree as git holds them:",
            ],
            dry.Details);
        Assert.Equal(AgentFoldMergeTests.Text((2, "AGENT 2")), OrchestrationKit.Read(worktree, Both));

        var applied = await RebaseAsync(kit, apply: true, Merging(Both));

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.StartsWith(
            "1 path(s) it changed that the main tree committed a change to, merged three-way, written into its worktree as its own change on the new base",
            applied.Details![0],
            StringComparison.Ordinal);
        Assert.Equal(merged, OrchestrationKit.Read(worktree, Both));
        Assert.Equal(to, kit.Record("ag").Base);
        Assert.Equal(to, await HeadAsync(worktree));
        Assert.Equal(seeded, File.ReadAllText(kit.Layout.SeedFile("ag")));

        var folded = await kit.FoldAsync("ag", apply: true);

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Contains($"  {Both}   (not handed to it, nor folded before)", folded.Details!);
        Assert.Equal(merged, OrchestrationKit.Read(kit.Main, Both));
    }

    /// <summary>A merge that conflicts is never taken, though --merge names it: the move is refused, and the conflicts are shown.</summary>
    [Fact]
    public async Task APathWhoseMergeConflicts_RefusesTheMove_ThoughItIsNamedToBeMerged_AndItsConflictsAreShown()
    {
        using var temp = new TempDirectory();
        var (kit, worktree, from) = await BothChangedAsync(temp, AgentFoldMergeTests.Text((9, "AGENT 9")), AgentFoldMergeTests.Text((9, "MAIN 9")));

        var refused = await RebaseAsync(kit, apply: true, Merging(Both));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            $"  '{Both}': the agent changed it, and the main tree committed a change to it since the agent's base {ReportText.Commit(from)}; a three-way merge of "
            + $"the two conflicts in 1 place(s), shown below: reconcile it by hand, then --settled {Both}",
            refused.Details!);
        Assert.Contains($"conflicts of '{Both}', 1 place(s) - as the main tree holds it, as both trees held it, as the agent holds it:", refused.Details!);
        Assert.Contains("  <<<<<<< the main tree", refused.Details!);
        Assert.DoesNotContain(refused.Details!, line => line.StartsWith("to merge the", StringComparison.Ordinal));
        Assert.Equal(AgentFoldMergeTests.Text((9, "AGENT 9")), OrchestrationKit.Read(worktree, Both));
        Assert.Equal(from, kit.Record("ag").Base);
    }

    /// <summary>
    /// A --merge naming a path the two commits hold alike merges nothing, and is refused as the misspelling it most likely
    /// is; one naming a path they hold differently that the agent did not change is let stand, so the line that finishes a
    /// stopped move runs whatever that move wrote. A path named both to be merged and as settled is a usage error.
    /// </summary>
    [Fact]
    public async Task AStrayMerge_IsRefused_AndAPathNamedBothToBeMergedAndAsSettled_IsAUsageError()
    {
        using var temp = new TempDirectory();
        var (kit, _, _) = await BothChangedAsync(temp, AgentFoldMergeTests.Text((2, "AGENT 2")), AgentFoldMergeTests.Text((9, "MAIN 9")));
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a file the agent did not change", Token);

        var stray = await RebaseAsync(kit, apply: false, Merging(Both, "b.txt", "src/typo.txt"));

        Assert.Equal(HarnessExit.Refused, stray.ExitCode);
        Assert.Equal(
            [
                "  --merge 'src/typo.txt' names no path the agent changed that the main tree committed a change to since, so it merges nothing: check its spelling",
            ],
            stray.Details!.Where(line => line.Contains("--merge '", StringComparison.Ordinal)));
        Assert.DoesNotContain(stray.Details!, line => line.Contains("'b.txt'", StringComparison.Ordinal));

        var usage = await RebaseAsync(kit, apply: false, new RebaseRequest { Merge = [Both], Settled = [$"{Both}/"] });

        Assert.Equal(HarnessExit.UsageError, usage.ExitCode);
        Assert.Equal($"'{Both}' is named with both --merge and --settled: a path is merged for you, or it was reconciled by hand, never both.", usage.Message);

        var unnameable = await RebaseAsync(kit, apply: false, Merging("../outside.txt"));

        Assert.Equal(HarnessExit.UsageError, unnameable.ExitCode);
        Assert.Contains("--merge", unnameable.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A move that stops part way names the whole command that finishes it - every path it was told to merge or keep - and
    /// running that finishes it, the merged file merged again to the same bytes.
    /// </summary>
    [Fact]
    public async Task AMoveThatStopsPartWay_NamesEveryOptionItWasGiven_AndRunningThatFinishesIt()
    {
        using var temp = new TempDirectory();
        var (kit, worktree, from) = await BothChangedAsync(temp, AgentFoldMergeTests.Text((2, "AGENT 2")), AgentFoldMergeTests.Text((9, "MAIN 9")));
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a file both change", Token);
        var to = await HeadAsync(kit.Main);
        var merged = AgentFoldMergeTests.Text((2, "AGENT 2"), (9, "MAIN 9"));
        var request = new RebaseRequest { Merge = [Both], Settled = ["b.txt"] };
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            ResetFailure = new HarnessException(HarnessExit.CommandFailed, "fatal: Unable to create 'index.lock': File exists."),
        };

        var stopped = await kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git).RebaseAsync(kit.Main, "o1", "ag", request, apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Equal(
            $"Moving the base of agent 'ag' of 'o1' from {ReportText.Commit(from)} to the main tree's HEAD {ReportText.Commit(to)} stopped part way: "
            + "fatal: Unable to create 'index.lock': File exists. Its worktree may hold part of what the new base brings; run "
            + $"'{ToolPackage.Command} rebase-agent o1 ag --apply --merge {Both} --settled b.txt' again once that is dealt with, and it finishes it.",
            stopped.Message);
        Assert.Equal(merged, OrchestrationKit.Read(worktree, Both));
        Assert.Equal(from, await HeadAsync(worktree));
        Assert.Equal(["--merge", Both, "--settled", "b.txt"], kit.Record("ag").MovingWith);

        // Every other command that meets the stopped move names that same command: the bare one is refused over these paths.
        var finishing = $"'{ToolPackage.Command} rebase-agent o1 ag --apply --merge {Both} --settled b.txt' finishes it.";
        Assert.EndsWith(finishing, (await kit.FoldAsync("ag", apply: false)).Message, StringComparison.Ordinal);
        Assert.EndsWith(
            finishing,
            (await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("b.txt"), apply: false, Token)).Message,
            StringComparison.Ordinal);
        Assert.EndsWith(finishing, (await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: false, Token)).Message, StringComparison.Ordinal);

        var finished = await RebaseAsync(kit, apply: true, request);

        Assert.True(finished.Succeeded, OrchestrationKit.Describe(finished));
        Assert.Equal(merged, OrchestrationKit.Read(worktree, Both));
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal(to, kit.Record("ag").Base);
        Assert.Null(kit.Record("ag").Moving);
        Assert.Null(kit.Record("ag").MovingWith);
    }

    /// <summary>
    /// The agent's file is asked to be what was weighed before the merge is written over it: one its session saved
    /// meanwhile stops the move part way, and is never written over.
    /// </summary>
    [Fact]
    public async Task AMergedFileTheAgentChangedAfterItWasWeighed_StopsTheMove_AndIsNotWrittenOver()
    {
        using var temp = new TempDirectory();
        var (kit, worktree, from) = await BothChangedAsync(temp, AgentFoldMergeTests.Text((2, "AGENT 2")), AgentFoldMergeTests.Text((9, "MAIN 9")));
        var saved = AgentFoldMergeTests.Text((2, "AGENT 2"), (4, "SAVED MEANWHILE"));
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            BeforeCheckOut = _ => OrchestrationKit.Write(worktree, Both, saved),
        };

        var stopped = await kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git).RebaseAsync(kit.Main, "o1", "ag", Merging(Both), apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Contains(
            $"stopped part way: '{Both}' holds other content in the agent's worktree now than when it was measured, so it was not written over. ",
            stopped.Message,
            StringComparison.Ordinal);
        Assert.Equal(saved, OrchestrationKit.Read(worktree, Both));
        Assert.Equal(from, kit.Record("ag").Base);
    }

    /// <summary>
    /// Whether the file can be run is merged with its content, from the two bases' entries and the agent's file: the main
    /// tree made it runnable and the agent edited it, so the merge can be run, and its line says so.
    /// </summary>
    [Fact]
    public async Task ARebaseMergesTheBit_FromTheTwoBasesEntriesAndTheAgentsFile()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit with a file.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var permissions = kit.Harness.FilePermissions;
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text());
        await kit.Harness.CommitAllAsync(kit.Main, "a file that cannot be run", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, Both, AgentFoldMergeTests.Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((9, "MAIN 9")));
        permissions.SetExecutable(Path.Combine(kit.Main, Both), executable: true);
        await kit.Harness.CommitAllAsync(kit.Main, "the main tree's change, which can be run", Token);

        var applied = await RebaseAsync(kit, apply: true, Merging(Both));

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains($"  {Both}   (--merge: 1 hunk(s) from the agent, 1 from the main tree; it can be run, as the main tree made it)", applied.Details!);
        Assert.Equal(AgentFoldMergeTests.Text((2, "AGENT 2"), (9, "MAIN 9")), OrchestrationKit.Read(worktree, Both));
        Assert.True(permissions.IsExecutable(Path.Combine(worktree, Both)), "The merged file cannot be run.");
    }

    /// <summary>A link is no file to merge: where the new base holds one, the path is offered no merge, saying why.</summary>
    [Fact]
    public async Task APathTheNewBaseHoldsAsALink_IsOfferedNoMerge_SayingWhy()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text());
        await kit.Harness.CommitAllAsync(kit.Main, "a file the main tree turns into a link", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(worktree, Both, AgentFoldMergeTests.Text((2, "AGENT 2")));

        // Set in the index, so the commit holds a link on a file system that makes none.
        var target = (await kit.Harness.RunGitAsync(kit.Main, ["rev-parse", "HEAD:a.txt"], Token)).StandardOutput.Trim();
        await kit.GitAsync(kit.Main, "update-index", "--cacheinfo", $"120000,{target},{Both}");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "the file becomes a link");

        var refused = await RebaseAsync(kit, apply: true, Merging(Both));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            $"  '{Both}': the agent changed it, and the main tree committed a change to it since the agent's base {ReportText.Commit(from)}; no three-way merge "
            + "is offered: one of its versions is a link, or is reached through one, and only files are merged",
            refused.Details!);
        Assert.Equal(AgentFoldMergeTests.Text((2, "AGENT 2")), OrchestrationKit.Read(worktree, Both));
    }

    /// <summary>
    /// A path the agent reaches through a link - its directory turned into a link to one outside its worktree - is offered
    /// no merge, named or not, and nothing is written through the link: a merge written there would land outside it.
    /// </summary>
    [Fact]
    public async Task APathTheAgentReachesThroughALink_IsOfferedNoMerge_AndNothingIsWrittenThroughIt()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text());
        await kit.Harness.CommitAllAsync(kit.Main, "a file the agent will reach through a link", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(outside.Path, "m.txt", AgentFoldMergeTests.Text((2, "AGENT 2")));
        Directory.Delete(Path.Combine(worktree, "src"), recursive: true);
        TestLinks.OrSkip(() => TestLinks.DirectoryLink(Path.Combine(worktree, "src"), outside.Path));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((9, "MAIN 9")));
        await kit.Harness.CommitAllAsync(kit.Main, "the main tree's change", Token);

        var refused = await RebaseAsync(kit, apply: true, Merging(Both));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{Both}': ", StringComparison.Ordinal)
            && line.EndsWith("; no three-way merge is offered: one of its versions is a link, or is reached through one, and only files are merged", StringComparison.Ordinal));
        Assert.Equal(AgentFoldMergeTests.Text((2, "AGENT 2")), OrchestrationKit.Read(outside.Path, "m.txt"));
    }

    /// <summary>
    /// A path the agent shares with the main tree, which both changed since, stays as its seed records it, named with
    /// --merge or not: nothing merged, its bytes untouched, and the --merge naming it is let stand - the two commits hold
    /// that path differently.
    /// </summary>
    [Fact]
    public async Task ASharedPathBothChanged_NamedWithMerge_StaysAsItsSeedRecordsIt_ItsBytesUntouched()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text());
        await kit.Harness.CommitAllAsync(kit.Main, "a file", Token);

        // Handed to the agent as the main tree holds it uncommitted, and so shared.
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((9, "MAIN 9")));
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((5, "MAIN 5"), (9, "MAIN 9")));
        await kit.Harness.CommitAllAsync(kit.Main, "the main tree's change, committed", Token);
        var agents = AgentFoldMergeTests.Text((2, "AGENT 2"), (9, "MAIN 9"));
        OrchestrationKit.Write(worktree, Both, agents);

        var applied = await RebaseAsync(kit, apply: true, Merging(Both));

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains(
            "and 1 path(s) it shares with the main tree, handed to it or folded, stayed as its seed records them; refresh-agent hands it any the main "
            + "tree moved since:",
            applied.Details!);
        Assert.Contains($"  {Both}", applied.Details!);
        Assert.DoesNotContain(applied.Details!, line => line.Contains("merged three-way", StringComparison.Ordinal));
        Assert.Equal(agents, OrchestrationKit.Read(worktree, Both));
    }

    /// <summary>
    /// The agent made the file runnable and the main tree edited it: the merge can be run, as the agent made it, its bit
    /// read from the agent's file and not from either base.
    /// </summary>
    [Fact]
    public async Task ARebaseMergesTheBit_AsTheAgentMadeIt_WhereTheMainTreeOnlyEditedTheFile()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit with a file.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var permissions = kit.Harness.FilePermissions;
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text());
        await kit.Harness.CommitAllAsync(kit.Main, "a file that cannot be run", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, Both, AgentFoldMergeTests.Text((2, "AGENT 2")));
        permissions.SetExecutable(Path.Combine(worktree, Both), executable: true);
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((9, "MAIN 9")));
        await kit.Harness.CommitAllAsync(kit.Main, "the main tree's edit", Token);

        var applied = await RebaseAsync(kit, apply: true, Merging(Both));

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains($"  {Both}   (--merge: 1 hunk(s) from the agent, 1 from the main tree; it can be run, as the agent made it)", applied.Details!);
        Assert.Equal(AgentFoldMergeTests.Text((2, "AGENT 2"), (9, "MAIN 9")), OrchestrationKit.Read(worktree, Both));
        Assert.True(permissions.IsExecutable(Path.Combine(worktree, Both)), "The merged file cannot be run.");
    }

    /// <summary>
    /// Windows keeps no execute bit, and none is read there: a rebase over a commit that made the file runnable merges its
    /// content and says nothing of a bit.
    /// </summary>
    [Fact]
    public async Task OnWindows_ARebaseOverACommitThatMadeTheFileRunnable_SaysNothingOfTheBit()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows keeps no execute bit with a file.");

        using var temp = new TempDirectory();
        var (kit, worktree, _) = await BothChangedAsync(temp, AgentFoldMergeTests.Text((2, "AGENT 2")), AgentFoldMergeTests.Text((9, "MAIN 9")));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((9, "MAIN NINE")));
        await kit.GitAsync(kit.Main, "add", "--", Both);
        await kit.GitAsync(kit.Main, "update-index", "--chmod=+x", Both);
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "the main tree's change, made runnable");

        var applied = await RebaseAsync(kit, apply: true, Merging(Both));

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains($"  {Both}   (--merge: 1 hunk(s) from the agent, 1 from the main tree)", applied.Details!);
        Assert.Equal(AgentFoldMergeTests.Text((2, "AGENT 2"), (9, "MAIN NINE")), OrchestrationKit.Read(worktree, Both));
    }

    /// <summary>
    /// A file the main tree renamed in case - its content kept or changed with it - comes into the agent's worktree under
    /// its new name, on a file system that finds a name in any case as on one that does not: never lost between the
    /// removal of the old name and the writing of the new, never taken for a file the agent made at the new name, and
    /// nothing left for a fold to take for the agent's work.
    /// </summary>
    [Theory]
    [InlineData("read me\n")]
    [InlineData("read me, renamed\n")]
    public async Task AFileTheMainTreeRenamedInCase_ComesIntoTheWorktreeUnderItsNewName(string renamed)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "docs/Readme.md", "read me\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a file", Token);
        var worktree = await kit.CreateAgentAsync("ag");
        await kit.GitAsync(kit.Main, "mv", "docs/Readme.md", "docs/README.md");
        OrchestrationKit.Write(kit.Main, "docs/README.md", renamed);
        await kit.GitAsync(kit.Main, "add", "--", "docs/README.md");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "renamed in case");
        var to = await HeadAsync(kit.Main);

        var applied = await RebaseAsync(kit, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal(to, kit.Record("ag").Base);
        Assert.Equal(
            ["README.md"],
            Directory.EnumerateFiles(Path.Combine(worktree, "docs")).Select(Path.GetFileName).Where(name => string.Equals(name, "readme.md", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(renamed, File.ReadAllText(Path.Combine(worktree, "docs", "README.md")));
        Assert.Empty((await kit.Harness.RunGitAsync(worktree, ["status", "--porcelain"], Token)).StandardOutput.Trim());
    }

    /// <summary>--merge reaches a move from the command line, once for each path, and a path no tree could hold is a usage error there.</summary>
    [Fact]
    public async Task TheCommandLine_TakesMergeOnceForEachPath()
    {
        using var temp = new TempDirectory();
        var (kit, worktree, _) = await BothChangedAsync(temp, AgentFoldMergeTests.Text((2, "AGENT 2")), AgentFoldMergeTests.Text((9, "MAIN 9")));

        var usage = await CliRunner.RunAsync(["rebase-agent", "o1", "ag", "--merge", "../outside.txt"], Token, workingDirectory: kit.Main);

        Assert.Equal(HarnessExit.UsageError, usage.ExitCode);

        var applied = await CliRunner.RunAsync(["rebase-agent", "o1", "ag", "--apply", "--merge", Both], Token, workingDirectory: kit.Main);

        Assert.True(applied.Succeeded, applied.StandardOutput + applied.StandardError);
        Assert.Equal(AgentFoldMergeTests.Text((2, "AGENT 2"), (9, "MAIN 9")), OrchestrationKit.Read(worktree, Both));
    }

    /// <summary>Folding, deleting, seeding and refreshing agent 'ag', each asked to write.</summary>
    private static async Task<CommandOutcome[]> EveryCommandButTheMoveAsync(OrchestrationKit kit)
        => [
            await kit.FoldAsync("ag", apply: true),
            await kit.DeleteAsync("ag", apply: true),
            await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: false, Token),
            await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: true, Token),
        ];

    private static Task<CommandOutcome> RebaseAsync(OrchestrationKit kit, bool apply, params string[] settled)
        => RebaseAsync(kit, apply, new RebaseRequest { Settled = settled });

    private static Task<CommandOutcome> RebaseAsync(OrchestrationKit kit, bool apply, RebaseRequest request)
        => kit.Harness.AgentService.RebaseAsync(kit.Main, "o1", "ag", request, apply, Token);

    private static async Task<string> HeadAsync(string tree) => (await new HarnessFactory().GitClient.ResolveCommitAsync(tree, "HEAD", Token))!;

    /// <summary>The real file system, except that one path cannot be looked at, as one this process may not read.</summary>
    private sealed class UnlookableFileSystem(IFileSystem inner, string hidden) : PassThroughFileSystem(inner)
    {
        public override PathKind KindOf(string path)
            => string.Equals(Path.GetFullPath(path), Path.GetFullPath(hidden), StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("Access to the path is denied.")
                : base.KindOf(path);
    }
}
