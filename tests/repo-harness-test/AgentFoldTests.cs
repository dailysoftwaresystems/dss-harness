using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Output;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// fold-agent: an agent's own work measured - told apart from what it was handed and from what the main tree did
/// meanwhile - and written into the main tree only where nothing can be lost: refused whole before anything is written, or
/// stopped part way (exit 21) where a file changes after it was weighed.
/// </summary>
public sealed class AgentFoldTests
{
    /// <summary>
    /// The agent's edits and new files are written, its deletion is made, with the directory it emptied, and what it
    /// was handed and left alone stays as the main tree has it; every handed path is counted by where it went.
    /// </summary>
    [Fact]
    public async Task AFold_WritesTheAgentsWork_RemovesItsDeletions_AndLeavesWhatItWasHandedOut()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var worktree = await kit.CreateAgentAsync("ag");

        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(worktree, "new.txt", "new\n");
        File.Delete(Path.Combine(worktree, "docs", "x.md"));

        var dry = await kit.FoldAsync("ag", apply: false);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.StartsWith("dry run: folding agent 'ag' of 'o1' writes 2 path(s) into the main tree, removes 1", dry.Message);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));

        var applied = await kit.FoldAsync("ag", apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains("1 path(s) it shares with the main tree, handed to it or folded before: 0 its own, 1 inherited, 0 already in the main tree, 0 deleted, 0 settled", applied.Details!);
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Equal("new\n", OrchestrationKit.Read(kit.Main, "new.txt"));
        Assert.False(Directory.Exists(Path.Combine(kit.Main, "docs")));
        Assert.Equal("one\nmain edit\n", OrchestrationKit.Read(kit.Main, "a.txt"));
        Assert.True(Directory.Exists(worktree));
    }

    /// <summary>
    /// A handed path the agent put back to what its base holds is its change all the same: git status no longer lists it,
    /// and the fold still weighs it, since every handed path is weighed.
    /// </summary>
    [Fact]
    public async Task AHandedPathTheAgentPutBack_IsItsChange_ThoughItsStatusNoLongerListsIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var worktree = await kit.CreateAgentAsync("ag");

        OrchestrationKit.Write(worktree, "a.txt", "one\n");

        var applied = await kit.FoldAsync("ag", apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("one\n", OrchestrationKit.Read(kit.Main, "a.txt"));
    }

    /// <summary>
    /// A handed path the main tree changed after the agent was handed it is refused, and with it the whole fold: the
    /// agent's other work is not written either.
    /// </summary>
    [Fact]
    public async Task AHandedPathTheMainTreeChangedSince_RefusesTheWholeFold()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "one\nagent edit\n");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit again\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            "  'a.txt': the main tree changed it after the agent was last handed it or folded, so writing the agent's copy would lose that change; a three-way merge "
            + "of the two conflicts in 1 place(s), shown below: reconcile it by hand, then --settled a.txt",
            refused.Details!);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Equal("one\nmain edit again\n", OrchestrationKit.Read(kit.Main, "a.txt"));
    }

    /// <summary>
    /// A path the agent was not handed is measured against its own base, never the main tree's HEAD: a sibling's fold the
    /// main tree still holds as an edit, or holds committed, is never written over, and the refusal says which it was.
    /// </summary>
    [Theory]
    [InlineData(false, "by an uncommitted edit, so writing the agent's copy would lose that change")]
    [InlineData(true, "by a commit, so writing the agent's copy would lose that change")]
    public async Task APathTheMainTreeChangedSinceTheBase_IsRefused_SayingHow(bool committed, string how)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nsibling edit\n");

        if (committed)
        {
            await kit.Harness.CommitAllAsync(kit.Main, "sibling", TestContext.Current.CancellationToken);
        }

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith("  'b.txt': the main tree changed it after the agent's base", StringComparison.Ordinal) && line.Contains(how, StringComparison.Ordinal));
        Assert.Contains(refused.Details!, line => line.Contains("--settled <path>", StringComparison.Ordinal));
        Assert.Equal("two\nsibling edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// A path whose mode alone the main tree committed since the agent's base - made runnable, its bytes as the base holds
    /// them - is said to have changed by a commit, as git compares a commit's entry, its mode among it: never by an
    /// uncommitted edit.
    /// </summary>
    [Fact]
    public async Task APathWhoseModeAloneTheMainTreeCommitted_IsSaidChangedByACommit()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var runnable = Path.Combine(kit.Main, "b.txt");

        // The index's mode is what a platform with no execute bit commits; the file's is what one with it does.
        await kit.GitAsync(kit.Main, "update-index", "--chmod=+x", "b.txt");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(runnable, File.GetUnixFileMode(runnable) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        await kit.Harness.CommitAllAsync(kit.Main, "b.txt runs", TestContext.Current.CancellationToken);

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            refused.Details!,
            line => line.StartsWith("  'b.txt': the main tree changed it after the agent's base", StringComparison.Ordinal)
                && line.Contains("by a commit, so writing the agent's copy would lose that change", StringComparison.Ordinal));
        Assert.DoesNotContain(refused.Details!, line => line.StartsWith("  'b.txt'", StringComparison.Ordinal) && line.Contains("uncommitted", StringComparison.Ordinal));
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// A path the agent shares nothing for that the main tree made, made a directory of, or removed since the agent's base
    /// is refused saying whether a commit or an uncommitted edit did it - the main tree's HEAD compared with the base entry
    /// by entry, as for a path the main tree changed - since the two are reconciled differently. A HEAD that holds nothing
    /// where the base held a file is a commit.
    /// </summary>
    /// <param name="change">What the main tree did: made the agent's new file, made a directory of a file of the base, or removed one.</param>
    /// <param name="committed">Whether the main tree committed it.</param>
    [Theory]
    [InlineData("made", false)]
    [InlineData("made", true)]
    [InlineData("directory", false)]
    [InlineData("directory", true)]
    [InlineData("removed", false)]
    [InlineData("removed", true)]
    public async Task APathTheMainTreeMadeOrRemovedSinceTheBase_IsSaidChangedByACommitOrAnUncommittedEdit(string change, bool committed)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var at = ReportText.Commit(kit.Record("ag").Base!);
        var path = change == "made" ? "new.txt" : "a.txt";
        OrchestrationKit.Write(worktree, path, "one\nthe agent's\n");

        switch (change)
        {
            case "made":
                OrchestrationKit.Write(kit.Main, path, "the main tree's\n");
                break;
            case "directory":
                File.Delete(Path.Combine(kit.Main, path));
                OrchestrationKit.Write(kit.Main, $"{path}/inner.txt", "the main tree's\n");
                break;
            default:
                File.Delete(Path.Combine(kit.Main, path));
                break;
        }

        if (committed)
        {
            await kit.Harness.CommitAllAsync(kit.Main, $"the main tree {change} {path}", TestContext.Current.CancellationToken);
        }

        var how = committed ? "by a commit," : "by an uncommitted edit,";
        var said = change switch
        {
            "made" => $"  '{path}' is not at the agent's base {at}, and the main tree made it since, {how} so writing the agent's copy would lose that change",
            "directory" => $"  '{path}' is at the agent's base {at}, and the main tree holds a directory there now, made {how} so writing the agent's copy would lose that change",
            _ => $"  '{path}' is at the agent's base {at}, and missing from the main tree, removed {how} so writing the agent's copy would lose that change",
        };

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith(said, StringComparison.Ordinal));
        Assert.DoesNotContain(
            refused.Details!,
            line => line.StartsWith($"  '{path}'", StringComparison.Ordinal) && line.Contains(committed ? "uncommitted" : "by a commit", StringComparison.Ordinal));
        Assert.Equal("one\nthe agent's\n", OrchestrationKit.Read(worktree, path));
    }

    /// <summary>
    /// Where a commit moved a path the agent shares nothing for and an uncommitted edit then moved it again, the refusal
    /// says both - by a commit alone only where the main tree's HEAD already holds what the main tree holds there now -
    /// since neither alone reconciles it: a sibling's committed change of a file the main tree then removed; a sibling's
    /// committed removal of a file the main tree then made a directory of; a commit inside a directory of the base the
    /// main tree then made a file of; a sibling's committed change the main tree then edited further; and a file a
    /// sibling's commit made that the main tree then edited.
    /// </summary>
    /// <param name="change">What the main tree did, uncommitted, after the sibling's commit.</param>
    [Theory]
    [InlineData("removed")]
    [InlineData("directory")]
    [InlineData("made-over-a-directory")]
    [InlineData("changed")]
    [InlineData("made")]
    public async Task APathACommitMovedAndAnUncommittedEditMovedAgain_IsSaidMovedByBoth(string change)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var at = ReportText.Commit(kit.Record("ag").Base!);
        var path = change switch
        {
            "made-over-a-directory" => "docs",
            "made" => "new.txt",
            "changed" => "b.txt",
            _ => "a.txt",
        };

        if (change == "made-over-a-directory")
        {
            Directory.Delete(Path.Combine(worktree, "docs"), recursive: true);
        }

        OrchestrationKit.Write(worktree, path, "one\nthe agent's\n");

        // What a sibling's fold committed.
        switch (change)
        {
            case "directory":
                File.Delete(Path.Combine(kit.Main, path));
                break;
            case "made-over-a-directory":
                OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nthe sibling's\n");
                break;
            default:
                OrchestrationKit.Write(kit.Main, path, "the sibling's\n");
                break;
        }

        await kit.Harness.CommitAllAsync(kit.Main, $"a sibling's fold, {change}", TestContext.Current.CancellationToken);

        // What the main tree did since, uncommitted.
        switch (change)
        {
            case "removed":
                File.Delete(Path.Combine(kit.Main, path));
                break;
            case "directory":
                OrchestrationKit.Write(kit.Main, $"{path}/inner.txt", "the main tree's\n");
                break;
            case "made-over-a-directory":
                Directory.Delete(Path.Combine(kit.Main, path), recursive: true);
                OrchestrationKit.Write(kit.Main, path, "the main tree's\n");
                break;
            default:
                OrchestrationKit.Write(kit.Main, path, "the sibling's\nand the main tree's\n");
                break;
        }

        var said = change switch
        {
            "removed" => $"  'a.txt' is at the agent's base {at}, and missing from the main tree, removed by an uncommitted edit, after a commit changed it, "
                + "so writing the agent's copy would lose that change",
            "directory" => $"  'a.txt' is at the agent's base {at}, and the main tree holds a directory there now, made by an uncommitted edit, after a commit "
                + "removed it, so writing the agent's copy would lose that change",
            "made-over-a-directory" => $"  'docs' is not at the agent's base {at}, and the main tree made it since, by an uncommitted edit, after a commit "
                + "changed the directory there, so writing the agent's copy would lose that change",
            "changed" => $"  'b.txt': the main tree changed it after the agent's base {at}, by an uncommitted edit, after a commit changed it, so writing "
                + "the agent's copy would lose that change",
            _ => $"  'new.txt' is not at the agent's base {at}, and the main tree made it since, by an uncommitted edit, after a commit made it, so writing "
                + "the agent's copy would lose that change",
        };

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith(said, StringComparison.Ordinal));
        Assert.DoesNotContain(refused.Details!, line => line.StartsWith($"  '{path}'", StringComparison.Ordinal) && line.Contains("by a commit", StringComparison.Ordinal));
    }

    /// <summary>
    /// What the main tree holds in the way of the agent's files is said made since the agent's base by a commit or by an
    /// uncommitted edit, as any other path the main tree moved since is: a file where one of the agent's files needs a
    /// directory, and each file of a directory one of them replaces.
    /// </summary>
    /// <param name="below">Whether the main tree holds a directory where the agent writes a file, rather than a file where it needs a directory.</param>
    /// <param name="committed">Whether the main tree committed what is in the way.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WhatTheMainTreeHoldsInTheWay_IsSaidMadeByACommitOrAnUncommittedEdit(bool below, bool committed)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var at = ReportText.Commit(kit.Record("ag").Base!);

        if (below)
        {
            OrchestrationKit.Write(kit.Main, "notes/a.md", "the sibling's\n");
            OrchestrationKit.Write(worktree, "notes", "the agent's\n");
        }
        else
        {
            OrchestrationKit.Write(kit.Main, "plan", "the sibling's\n");
            OrchestrationKit.Write(worktree, "plan/step.md", "the agent's\n");
        }

        if (committed)
        {
            await kit.Harness.CommitAllAsync(kit.Main, "a sibling's fold", TestContext.Current.CancellationToken);
        }

        var how = committed ? "by a commit" : "by an uncommitted edit";
        var said = below
            ? $"  'notes': the main tree holds a directory there with files the agent did not delete - notes/a.md (made since the agent's base {at} {how}) - "
                + "which writing it would remove"
            : $"  'plan/step.md' needs a directory at 'plan', where the main tree holds a file the agent did not delete, made since the agent's base {at} {how}";

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(said, refused.Details!);
    }

    /// <summary>
    /// A file of the base's in the way of the agent's - one it turned into a directory, whose removal the fold does not
    /// make - is said changed since the agent's base where the main tree changed it, and nothing more where the main tree
    /// holds it as the base does: it did nothing there since.
    /// </summary>
    /// <param name="changed">Whether the main tree changed the file, uncommitted.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFileOfTheBaseInTheWay_IsSaidChangedSinceTheBase_OnlyWhereTheMainTreeChangedIt(bool changed)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var at = ReportText.Commit(kit.Record("ag").Base!);
        File.Delete(Path.Combine(worktree, "a.txt"));
        OrchestrationKit.Write(worktree, "a.txt/step.md", "the agent's\n");

        if (changed)
        {
            OrchestrationKit.Write(kit.Main, "a.txt", "one\nthe main tree's\n");
        }

        // Unchanged, the agent's removal of it is held back by naming it settled, where nothing was reconciled.
        var refused = await kit.FoldAsync("ag", apply: true, changed ? [] : ["a.txt"]);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            "  'a.txt/step.md' needs a directory at 'a.txt', where the main tree holds a file the agent did not delete"
                + (changed ? $", changed since the agent's base {at} by an uncommitted edit" : string.Empty),
            refused.Details!);
    }

    /// <summary>
    /// The help and the architecture say how a refusal of a main-tree path changed since says it: a path the agent shares,
    /// changed after it was last handed it or folded; any other, since the agent's base, by a commit, by an uncommitted
    /// edit or by both, which the message says - and so for what the main tree holds in the way of the agent's files.
    /// </summary>
    [Fact]
    public async Task TheHelpAndTheArchitectureSayHowAMainTreePathChangedSinceIsSaid()
    {
        var help = Prose.Words((await CliRunner.RunAsync(["help", "agents"], TestContext.Current.CancellationToken)).StandardOutput);

        Assert.Contains(
            "The whole fold is refused, and nothing written, where the main tree changed a path the agent shares after the agent was "
                + "last handed it or folded, or any other of its paths since the agent's base - made, changed, made a directory of or "
                + "removed by a commit, by an uncommitted edit or by both, which the message says - where the agent's HEAD is off its base",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            "where what the main tree holds is in the way of what it writes - a file where a directory goes, or files the agent did "
                + "not delete in a directory a file replaces, each said made or changed since the agent's base as any other path is - "
                + "and for an anchor registry",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            "A refusal of a main-tree path the agent shares nothing for, made, changed, made a directory of or removed since the "
                + "agent's base, says whether a commit, an uncommitted edit or both did it, since the two are reconciled differently: a "
                + "commit alone where the main tree's HEAD already holds what the main tree holds there now - nothing where it was "
                + "removed, a directory where one was made, and where a file was made or changed, that file as the main tree holds it - "
                + "its entry compared with the base's as git compares a commit, so a commit of a mode alone is a commit; an uncommitted "
                + "edit alone where HEAD holds what the base holds; and both otherwise, the edit said after what the commit did. A file "
                + "of the main tree in the way of the agent's - where one of its files needs a directory, or in a directory one "
                + "replaces - is said made or changed since the base the same way (`Came.How`, the one wording of both), and a shared "
                + "path's refusal says the main tree changed it after the agent was last handed it or folded, which the seed records "
                + "alike.",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A path a fold wrote - shared with the main tree by that fold, never handed to the agent - that the main tree changed
    /// or removed since is refused in words true of every shared path: what the main tree did after the agent was last
    /// handed it or folded, never that it was handed.
    /// </summary>
    [Theory]
    [InlineData(false, "  'b.txt': the main tree changed it after the agent was last handed it or folded, so writing the agent's copy would lose that change")]
    [InlineData(true, "  'b.txt' was in both trees when the agent was last handed it or folded, and the main tree no longer holds it")]
    public async Task AFoldedPathTheMainTreeChangedSince_IsRefused_InWordsTrueOfEverySharedPath(bool removed, string said)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        Assert.True((await kit.FoldAsync("ag", apply: true)).Succeeded);

        if (removed)
        {
            File.Delete(Path.Combine(kit.Main, "b.txt"));
        }
        else
        {
            OrchestrationKit.Write(kit.Main, "b.txt", "two\nagent edit\nmain edit\n");
        }

        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit again\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith(said, StringComparison.Ordinal));
        Assert.DoesNotContain(refused.Details!, line => line.Contains("handed to the agent", StringComparison.Ordinal));
    }

    /// <summary>
    /// A fold after the main tree committed writes the agent's own work and leaves what the main tree committed meanwhile:
    /// a path the agent never changed is not its work, whatever the main tree's HEAD holds now, and a path it was handed and
    /// left alone stays as the main tree changed and committed it since.
    /// </summary>
    [Fact]
    public async Task AFoldAfterTheMainTreeCommitted_WritesOnlyTheAgentsOwnWork()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nhanded\n");
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\ncommitted\n");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nhanded\nchanged since\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", TestContext.Current.CancellationToken);

        var applied = await kit.FoldAsync("ag", apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Equal("one\ncommitted\n", OrchestrationKit.Read(kit.Main, "a.txt"));
        Assert.Equal("x\nhanded\nchanged since\n", OrchestrationKit.Read(kit.Main, "docs/x.md"));
    }

    /// <summary>
    /// --settled names a path reconciled in the main tree by hand, a deletion among them: once the rest of the agent's work
    /// is written, the main tree's copy - a file, or its absence - is handed to the agent and recorded as shared, so its
    /// next fold leaves the path out as inherited, and the command line that ran once runs again. (At 0.6.13 the path was
    /// left out, nothing handed or recorded, and the agent's next fold refused it again.)
    /// </summary>
    [Fact]
    public async Task SettledPaths_AreHandedTheMainTreesCopy_AndRecorded_SoTheAgentsNextFoldLeavesThemOut()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "a.txt", "one\nagent edit\n");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(worktree, "new.txt", "new\n");
        File.Delete(Path.Combine(worktree, "docs", "x.md"));
        File.Delete(Path.Combine(kit.Main, "a.txt"));
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmerged by hand\n");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nmain edit\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith("  'b.txt': the main tree changed it after the agent's base", StringComparison.Ordinal)
            && line.EndsWith("so writing the agent's copy would lose that change; a three-way merge of the two conflicts in 1 place(s), shown below: reconcile it by hand, then --settled b.txt", StringComparison.Ordinal));
        Assert.Contains(refused.Details!, line => line.StartsWith("  'docs/x.md': the main tree changed it after the agent's base", StringComparison.Ordinal) && line.EndsWith("so removing it would lose that change", StringComparison.Ordinal));

        string[] settled = ["a.txt", "b.txt", "docs/x.md"];
        var dry = await kit.FoldAsync("ag", apply: false, settled);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.StartsWith("dry run: folding agent 'ag' of 'o1' writes 1 path(s) into the main tree, hands the agent 3 settled by hand, removes 0 and applies 0 row(s)", dry.Message);
        Assert.Contains("3 path(s) declared settled by hand: the main tree's copy to hand to the agent and record as shared:", dry.Details!);
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(worktree, "b.txt"));

        var applied = await kit.FoldAsync("ag", apply: true, settled);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.StartsWith("folded agent 'ag' of 'o1': wrote 1 path(s) into the main tree, handed the agent 3 settled by hand, removed 0 and applied 0 row(s)", applied.Message);
        Assert.Equal(
            [
                "3 path(s) declared settled by hand: the main tree's copy handed to the agent and recorded as shared:",
                "  a.txt   (removed: the main tree holds no file there)",
                "  b.txt",
                "  docs/x.md",
                "the agent's session must read these again before it writes them: a write made from what it held before would fold as its change, over what the "
                    + "main tree brought",
            ],
            applied.Details!.Take(5));
        Assert.Equal("two\nmerged by hand\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Equal("x\nmain edit\n", OrchestrationKit.Read(kit.Main, "docs/x.md"));
        Assert.False(File.Exists(Path.Combine(kit.Main, "a.txt")));
        Assert.Equal("new\n", OrchestrationKit.Read(kit.Main, "new.txt"));
        Assert.Equal("two\nmerged by hand\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal("x\nmain edit\n", OrchestrationKit.Read(worktree, "docs/x.md"));
        Assert.False(File.Exists(Path.Combine(worktree, "a.txt")));

        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.Equal(await AgentServiceTests.DigestAsync(kit.Main, "b.txt"), seed.Paths["b.txt"]);
        Assert.Equal(await AgentServiceTests.DigestAsync(kit.Main, "docs/x.md"), seed.Paths["docs/x.md"]);
        Assert.Contains("a.txt", seed.Absent!);

        var again = await kit.FoldAsync("ag", apply: true, settled);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Contains("4 inherited path(s) left out; 0 path(s) are its own:", again.Details!);

        var next = await kit.FoldAsync("ag", apply: true);

        Assert.True(next.Succeeded, OrchestrationKit.Describe(next));
        Assert.Contains("4 inherited path(s) left out; 0 path(s) are its own:", next.Details!);
    }

    /// <summary>
    /// A shared path only the main tree changed since the agent was handed it is not the agent's work, and a fold leaves it
    /// as inherited; settled by hand, the main tree's copy is the reconciliation, and it is handed and recorded.
    /// </summary>
    [Fact]
    public async Task ASettledSharedPathOnlyTheMainTreeChanged_IsHanded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\nreconciled\n");

        var unsettled = await kit.FoldAsync("ag", apply: false);

        Assert.True(unsettled.Succeeded, OrchestrationKit.Describe(unsettled));
        Assert.Contains("1 inherited path(s) left out; 0 path(s) are its own:", unsettled.Details!);

        var applied = await kit.FoldAsync("ag", apply: true, "a.txt");

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal(["1 path(s) declared settled by hand: the main tree's copy handed to the agent and recorded as shared:", "  a.txt"], applied.Details!.Take(2));
        Assert.Equal("one\nmain edit\nreconciled\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal(await AgentServiceTests.DigestAsync(kit.Main, "a.txt"), kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths["a.txt"]);
    }

    /// <summary>
    /// A settled path both trees already hold alike is recorded as shared, and nothing is copied. (At 0.6.13 "settled" was
    /// asked first, and the path was left out unrecorded.)
    /// </summary>
    [Fact]
    public async Task ASettledPathBothTreesHoldAlike_IsRecorded_AndNothingCopied()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nreconciled\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nreconciled\n");

        var applied = await kit.FoldAsync("ag", apply: true, "b.txt");

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.DoesNotContain(applied.Details!, line => line.Contains("declared settled by hand", StringComparison.Ordinal));
        Assert.Equal(["and 1 path(s) the main tree already holds as it does, with nothing to write:", "  b.txt"], applied.Details!.SkipWhile(line => !line.StartsWith("and 1 path(s) the main tree", StringComparison.Ordinal)).Take(2));
        Assert.Equal(await AgentServiceTests.DigestAsync(kit.Main, "b.txt"), kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths["b.txt"]);
    }

    /// <summary>
    /// A settled path only the agent changed is refused: the main tree holds it as the agent's base does, so nothing was
    /// reconciled there, and handing the main tree's copy - or removing a file the agent made - would undo the agent's work.
    /// Its change stays in its worktree, and nothing is written. (At 0.6.13 such a path was left out of the fold, silently.)
    /// </summary>
    [Fact]
    public async Task ASettledPathOnlyTheAgentChanged_IsRefused_AndItsChangeStays()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(worktree, "made.txt", "the agent's\n");
        OrchestrationKit.Write(worktree, "new.txt", "new\n");
        var at = OrchestrationReports.Base(kit.Record("ag").Base!);

        var refused = await kit.FoldAsync("ag", apply: true, "b.txt", "made.txt");

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            [
                "2 problem(s), and nothing was written:",
                $"  --settled 'b.txt': the main tree holds it as the agent's base {at} does, so nothing was reconciled there, and handing the agent the main "
                    + "tree's copy would undo the agent's change: drop it",
                $"  --settled 'made.txt': the main tree holds no file there, as the agent's base {at} holds none, so nothing was reconciled there, and "
                    + "removing the agent's copy would undo the agent's change: drop it",
            ],
            refused.Details!.Take(3));
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal("the agent's\n", OrchestrationKit.Read(worktree, "made.txt"));
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.False(File.Exists(Path.Combine(kit.Main, "new.txt")));
    }

    /// <summary>
    /// A settled path the agent shares with the main tree that only the agent changed since is refused as well, saying the
    /// main tree holds it as when the agent last shared it.
    /// </summary>
    [Fact]
    public async Task ASharedSettledPathOnlyTheAgentChanged_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nfirst pass\n");
        Assert.True((await kit.FoldAsync("ag", apply: true)).Succeeded);
        OrchestrationKit.Write(worktree, "b.txt", "two\nsecond pass\n");

        var refused = await kit.FoldAsync("ag", apply: true, "b.txt");

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            "  --settled 'b.txt': the main tree holds it as it did when the agent last shared it, so nothing was reconciled there, and handing the agent "
                + "the main tree's copy would undo the agent's change: drop it",
            refused.Details!);
        Assert.Equal("two\nsecond pass\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Equal("two\nfirst pass\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// What the main tree hands a settled path is never written over the agent's own: a directory of its files where the
    /// main tree holds a file refuses the fold, and nothing is written in either tree.
    /// </summary>
    [Fact]
    public async Task ASettledHandOverThatWouldRemoveTheAgentsOwnFiles_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        File.Delete(Path.Combine(worktree, "b.txt"));
        OrchestrationKit.Write(worktree, "b.txt/inner.txt", "the agent's\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmerged by hand\n");

        var refused = await kit.FoldAsync("ag", apply: true, "b.txt");

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            "  --settled 'b.txt': the agent holds a directory there with files of its own - b.txt/inner.txt - which handing it the main tree's copy would remove",
            refused.Details!);
        Assert.Equal("the agent's\n", OrchestrationKit.Read(worktree, "b.txt/inner.txt"));
        Assert.Equal("two\nmerged by hand\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// A settled hand-over is guarded as every hand-over is: a file the agent's session writes after it was weighed is never
    /// written over, and the fold stops there, exit 21, with what it wrote into the main tree said and recorded.
    /// </summary>
    [Fact]
    public async Task ASettledHandOver_StopsWhereTheAgentsFileChangedSinceItWasWeighed()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(worktree, "new.txt", "new\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmerged by hand\n");
        var copy = Path.Combine(worktree, "b.txt");

        // Its third reading is the one that asks, before the copy, whether it is what was weighed: the session writes first.
        var agents = kit.Harness.Agents(
            new AgentFoldMergeTests.MeanwhileFileSystem(kit.Harness.FileSystem, copy, 3, () => File.WriteAllText(copy, "two\nthe session's edit\n")),
            kit.Harness.AnchorRegistryService);

        var stopped = await agents.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances { Settled = ["b.txt"] }, apply: true, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith(
            "Folding agent 'ag' of 'o1' stopped part way, after writing 1 of 1 path(s) and removing 0 of 0, then handing the agent 0 of 1 settled by hand: "
                + "'b.txt' holds other content in the agent's worktree now than when it was measured, so it was not written over. What it wrote is in the "
                + "main tree and recorded as shared",
            stopped.Message,
            StringComparison.Ordinal);
        Assert.Equal("two\nthe session's edit\n", OrchestrationKit.Read(worktree, "b.txt"));

        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.DoesNotContain("b.txt", seed.Paths.Keys);
        Assert.Contains("new.txt", seed.Paths.Keys);
    }

    /// <summary>A settled path the fold does not weigh settles nothing, and is refused as the misspelling it most likely is.</summary>
    [Fact]
    public async Task ASettledPathTheFoldDoesNotWeigh_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");

        var refused = await kit.FoldAsync("ag", apply: true, "bee.txt");

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("  --settled 'bee.txt' names no path this fold weighs, so it settles nothing: check its spelling", refused.Details!);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// What the main tree already holds as the agent does is neither written nor refused, so a fold run again - after one
    /// that stopped part way, or went through - finds its own writes already in.
    /// </summary>
    [Fact]
    public async Task AFoldRunAgain_FindsItsOwnWritesShared_AndWritesNothing()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        Assert.True((await kit.FoldAsync("ag", apply: true)).Succeeded);

        var again = await kit.FoldAsync("ag", apply: true);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Contains("1 inherited path(s) left out; 0 path(s) are its own:", again.Details!);
        Assert.Contains("b.txt", kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths.Keys);
    }

    /// <summary>
    /// A change the main tree already holds as the agent does - made there by hand, or by a fold that stopped before it
    /// could record it - is neither written nor refused, and is recorded as shared from then on.
    /// </summary>
    [Fact]
    public async Task AChangeTheMainTreeAlreadyHolds_IsAlreadyIn_AndRecordedAsShared()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nagent edit\n");

        var folded = await kit.FoldAsync("ag", apply: true);

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Contains("and 1 path(s) the main tree already holds as it does, with nothing to write:", folded.Details!);
        Assert.Contains("b.txt", kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths.Keys);
    }

    /// <summary>
    /// A path the agent removed that its base never held has no baseline, so removing it from the main tree is refused
    /// rather than guessed safe.
    /// </summary>
    [Fact]
    public async Task ADeletionWithNoBaseline_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "u.txt", "agent's\n");
        await kit.GitAsync(worktree, "add", "u.txt");
        File.Delete(Path.Combine(worktree, "u.txt"));
        OrchestrationKit.Write(kit.Main, "u.txt", "main's\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith("  the agent deleted 'u.txt', which its base", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(kit.Main, "u.txt")));
    }

    /// <summary>A commit made inside the agent hides its changes from status, the only list a fold reads, so the fold is refused.</summary>
    [Fact]
    public async Task ACommitInsideTheAgent_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        await kit.Harness.CommitAllAsync(worktree, "agent commit", TestContext.Current.CancellationToken);

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("a commit made inside it hides its changes from git status", refused.Message);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// A main tree file checked out with other line endings than its blob is not a change: it is measured through the
    /// clean filters git compares a working file through, never by its raw bytes.
    /// </summary>
    [Fact]
    public async Task ALineEndingConversion_IsNotTakenForAChange()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, ".gitattributes", "*.txt text eol=crlf\n");
        await kit.Harness.CommitAllAsync(kit.Main, "attributes", TestContext.Current.CancellationToken);
        var worktree = await kit.CreateAgentAsync("ag");

        // Converted after the agent was made, the main tree's copy was never handed to it, so it is weighed against the
        // agent's base: git status lists it for its new size, and only the clean filter shows it is the same.
        OrchestrationKit.Write(kit.Main, "b.txt", "two\r\n");
        OrchestrationKit.Write(worktree, "b.txt", "two\r\nagent edit\r\n");

        var applied = await kit.FoldAsync("ag", apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("two\r\nagent edit\r\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>A repository made inside the agent's worktree is a directory to it, and a fold moves files only.</summary>
    [Fact]
    public async Task ARepositoryInsideTheAgent_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var nested = Directory.CreateDirectory(Path.Combine(worktree, "nested")).FullName;
        await kit.Harness.InitializeGitRepositoryAsync(nested, TestContext.Current.CancellationToken);

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("  'nested' is a directory in the agent's worktree - a submodule, or a repository of its own - and a fold moves files only", refused.Details!);
    }

    /// <summary>
    /// What an agent's worktree keeps in its slots - a sweep's workers, each a repository of its own, and the copies its
    /// legs work in - is no work of the agent's: a fold weighs none of it, so neither a repository there nor a file is
    /// refused or written, and the agent's own work goes in.
    /// </summary>
    [Fact]
    public async Task WhatAnAgentsSlotsHold_IsNeverWeighed_AWorkersRepositoryAmongIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var worker = Directory.CreateDirectory(HarnessLayout.InSlot(worktree, HarnessLayout.MutationsDirectoryName, "357e24cw-1")).FullName;

        await kit.Harness.InitializeGitRepositoryAsync(worker, TestContext.Current.CancellationToken);
        OrchestrationKit.Write(worktree, ".legs/357e24c/src/a.c", "a leg's copy\n");
        OrchestrationKit.Write(worktree, "new.txt", "new\n");

        var dry = await kit.FoldAsync("ag", apply: false);
        var applied = await kit.FoldAsync("ag", apply: true);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.StartsWith("dry run: folding agent 'ag' of 'o1' writes 1 path(s) into the main tree, removes 0", dry.Message);
        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("new\n", OrchestrationKit.Read(kit.Main, "new.txt"));
        Assert.False(Directory.Exists(Path.Combine(kit.Main, HarnessLayout.MutationsDirectoryName, "357e24cw-1")));
        Assert.False(Directory.Exists(Path.Combine(kit.Main, HarnessLayout.LegsDirectoryName, "357e24c")));
    }

    /// <summary>
    /// A path of the agent's that leads, in the main tree, through a link out of it is never written through, nor handed
    /// from: --settled never reaches outside the main tree, so it is asked first.
    /// </summary>
    [Fact]
    public async Task APathLeadingOutOfTheMainTreeThroughALink_IsRefused()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "lib/f.txt", "agent's\n");
        TestLinks.DirectoryLink(Path.Combine(kit.Main, "lib"), outside.Path);

        var refused = await kit.FoldAsync("ag", apply: true);
        var settled = await kit.FoldAsync("ag", apply: true, "lib/f.txt");

        foreach (var outcome in new[] { refused, settled })
        {
            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.Contains(outcome.Details!, line => line.StartsWith("  'lib/f.txt' leads out of the main tree, through a link in the main tree", StringComparison.Ordinal));
        }

        Assert.False(File.Exists(Path.Combine(outside.Path, "f.txt")));
    }

    /// <summary>A symbolic link among the agent's changes is refused: copied, it would be the file it leads to.</summary>
    [Fact]
    public async Task ASymbolicLinkAmongTheAgentsChanges_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        TestLinks.OrSkip(() => File.CreateSymbolicLink(Path.Combine(worktree, "link.txt"), Path.Combine(worktree, "b.txt")));

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith("  'link.txt' is reached through a symbolic link or junction in the agent's worktree", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(kit.Main, "link.txt")));
    }

    /// <summary>An execute bit the agent set is its change, though the content is unchanged, and the fold writes it.</summary>
    [Fact]
    public async Task AnExecuteBitTheAgentSet_IsFolded()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows files have no execute bit.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var script = Path.Combine(worktree, "b.txt");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, File.GetUnixFileMode(script) | UnixFileMode.UserExecute);
        }

        var applied = await kit.FoldAsync("ag", apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains("  b.txt   (not handed to it, nor folded before)", applied.Details!);

        if (!OperatingSystem.IsWindows())
        {
            Assert.True((File.GetUnixFileMode(Path.Combine(kit.Main, "b.txt")) & UnixFileMode.UserExecute) != 0);
        }
    }

    /// <summary>A closed agent is never folded again: what is left in it may be the debris of a removal that stopped part way.</summary>
    [Fact]
    public async Task AClosedAgent_IsNeverFoldedAgain()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        kit.Harness.OrchestrationStore.WriteAgent(kit.Layout, kit.Record("ag") with
        {
            State = Core.Orchestration.AgentStates.Closed,
            Abandoned = false,
            Closing = new Core.Orchestration.AgentClosing { At = DateTimeOffset.UtcNow, Evidence = string.Empty, Stamp = "1:1", Held = [] },
        });

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' is closed: its deletion began, and it is never seeded, refreshed or folded again.", refused.Message);
    }

    /// <summary>
    /// An agent's file that holds other content when the fold is written than when it was measured is not written: what
    /// is written is what was weighed, and the fold says where it stopped.
    /// </summary>
    [Fact]
    public async Task AnAgentsFileChangedSinceItWasMeasured_IsNotWritten()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var shifting = new ShiftingFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "b.txt"), "two\nhalf written\n");
        var agents = kit.Harness.Agents(shifting, kit.Harness.AnchorRegistryService);

        var stopped = await agents.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith("Folding agent 'ag' of 'o1' stopped part way, after writing 0 of 1 path(s)", stopped.Message);
        Assert.Contains("holds other content now than when it was measured", OrchestrationKit.Describe(stopped));
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>A path that cannot be looked at is never read as absent - taken for the agent's deletion - and the fold stops before anything is written.</summary>
    [Fact]
    public async Task APathThatCannotBeLookedAt_StopsTheFoldBeforeAnythingIsWritten()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(worktree, "new.txt", "new\n");
        var agents = kit.Harness.Agents(new UnaskableFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "b.txt")), kit.Harness.AnchorRegistryService);

        var failed = await agents.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, failed.ExitCode);
        Assert.Contains("what it changed cannot be read", failed.Message);
        Assert.False(File.Exists(Path.Combine(kit.Main, "new.txt")));
    }

    /// <summary>
    /// Two agents of one base that both add a file: the first fold writes it, and the second is refused rather than write
    /// its own over the first's.
    /// </summary>
    [Fact]
    public async Task AFileTwoAgentsBothAdd_IsRefusedToTheSecondFold()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var first = await kit.CreateAgentAsync("a1");
        var second = await kit.CreateAgentAsync("a2");
        OrchestrationKit.Write(first, "tests/new.c", "first\n");
        OrchestrationKit.Write(second, "tests/new.c", "second\n");
        Assert.True((await kit.FoldAsync("a1", apply: true)).Succeeded);

        var refused = await kit.FoldAsync("a2", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith("  'tests/new.c' is not at the agent's base", StringComparison.Ordinal)
            && line.EndsWith(
                "and the main tree made it since, by an uncommitted edit, so writing the agent's copy would lose that change; no three-way merge "
                    + "is offered: both trees made it, and neither held a file there before",
                StringComparison.Ordinal));
        Assert.Equal("first\n", OrchestrationKit.Read(kit.Main, "tests/new.c"));
    }

    /// <summary>A file the agent was handed and deleted is removed from the main tree where the main tree still holds what it handed.</summary>
    [Fact]
    public async Task AHandedFileTheAgentDeleted_IsRemoved_WhereTheMainTreeKeptIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var worktree = await kit.CreateAgentAsync("ag");
        File.Delete(Path.Combine(worktree, "a.txt"));

        var applied = await kit.FoldAsync("ag", apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.False(File.Exists(Path.Combine(kit.Main, "a.txt")));
    }

    /// <summary>A file the agent was handed and deleted is refused where the main tree changed it since: removing it would lose that change.</summary>
    [Fact]
    public async Task AHandedFileTheAgentDeleted_IsRefused_WhereTheMainTreeChangedItSince()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var worktree = await kit.CreateAgentAsync("ag");
        File.Delete(Path.Combine(worktree, "a.txt"));
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\nand another\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("  'a.txt': the main tree changed it after the agent was last handed it or folded, so removing it would lose that change", refused.Details!);
        Assert.Equal("one\nmain edit\nand another\n", OrchestrationKit.Read(kit.Main, "a.txt"));
    }

    /// <summary>
    /// An agent a review sends back, which puts back a path its fold wrote and deletes a file its fold added: the next fold
    /// weighs both against what the first fold left, and folds them - never missed because the agent's status no longer
    /// lists them.
    /// </summary>
    [Fact]
    public async Task AnAgentSentBack_FoldsItsUndoingOfWhatItsFoldWrote()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(worktree, "new.txt", "new\n");
        Assert.True((await kit.FoldAsync("ag", apply: true)).Succeeded);

        OrchestrationKit.Write(worktree, "b.txt", "two\n");
        File.Delete(Path.Combine(worktree, "new.txt"));

        var again = await kit.FoldAsync("ag", apply: true);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.False(File.Exists(Path.Combine(kit.Main, "new.txt")));
    }

    /// <summary>An agent a review sends back, which changes again a path its fold wrote, has that change folded, never refused as the main tree's.</summary>
    [Fact]
    public async Task AnAgentSentBack_ThatChangesAFoldedPathAgain_IsFolded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nfirst pass\n");
        Assert.True((await kit.FoldAsync("ag", apply: true)).Succeeded);
        OrchestrationKit.Write(worktree, "b.txt", "two\nsecond pass\n");

        var again = await kit.FoldAsync("ag", apply: true);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Equal("two\nsecond pass\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>A main-tree file saved between the fold's measuring and its writing is never written over: the fold stops there, saying so.</summary>
    [Fact]
    public async Task AMainTreeFileSavedAfterItWasMeasured_IsNeverWrittenOver()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var agents = kit.Harness.Agents(new ShiftingFileSystem(kit.Harness.FileSystem, Path.Combine(kit.Main, "b.txt"), "two\nsaved meanwhile\n"), kit.Harness.AnchorRegistryService);

        var stopped = await agents.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Contains("holds other content in the main tree now than when it was measured, so it was not written over", stopped.Message);
        Assert.Equal("two\nsaved meanwhile\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>An execute bit the main tree set since the agent's base is its change: the agent's copy, with the base's mode, is never written over it.</summary>
    [Fact]
    public async Task AnExecuteBitTheMainTreeSetSince_IsNeverWrittenOver()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows files have no execute bit.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var main = Path.Combine(kit.Main, "b.txt");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(main, File.GetUnixFileMode(main) | UnixFileMode.UserExecute);
        }

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith("  'b.txt': the main tree changed it after the agent's base", StringComparison.Ordinal));
        Assert.True(OperatingSystem.IsWindows() || (File.GetUnixFileMode(main) & UnixFileMode.UserExecute) != 0);
    }

    /// <summary>
    /// A file committed with carriage returns, in a repository that converts line endings, is no change of the main tree's
    /// where the main tree left it alone: compared as git status compares, through the index's rules, never by hashing the
    /// file on its own.
    /// </summary>
    [Fact]
    public async Task AFileCommittedWithCarriageReturns_IsNoChangeOfTheMainTrees_WhereItWasLeftAlone()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "crlf.txt", "one\r\n");
        await kit.GitAsync(kit.Main, "-c", "core.autocrlf=false", "add", "crlf.txt");
        await kit.GitAsync(kit.Main, "-c", "core.autocrlf=false", "commit", "-q", "-m", "a file with carriage returns");
        await kit.GitAsync(kit.Main, "config", "core.autocrlf", "true");
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "crlf.txt", "one\r\nagent edit\r\n");

        var applied = await kit.FoldAsync("ag", apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("one\r\nagent edit\r\n", OrchestrationKit.Read(kit.Main, "crlf.txt"));
    }

    /// <summary>A deletion in the main tree's uncommitted state is handed to the agent as one - made in its worktree too - and its fold leaves it out.</summary>
    [Fact]
    public async Task AMainTreeDeletion_IsHandedToTheAgent_AndItsFoldLeavesItOut()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        File.Delete(Path.Combine(kit.Main, "docs", "x.md"));
        var worktree = await kit.CreateAgentAsync("ag");

        Assert.False(File.Exists(Path.Combine(worktree, "docs", "x.md")));
        Assert.Contains("docs/x.md", kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Absent ?? []);

        var applied = await kit.FoldAsync("ag", apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains("1 inherited path(s) left out; 0 path(s) are its own:", applied.Details!);
        Assert.False(File.Exists(Path.Combine(kit.Main, "docs", "x.md")));
    }

    /// <summary>A file the agent makes again where it was handed a deletion is its work, and is folded into the main tree that still lacks it.</summary>
    [Fact]
    public async Task AFileTheAgentMakesAgain_WhereItWasHandedADeletion_IsFolded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        File.Delete(Path.Combine(kit.Main, "docs", "x.md"));
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "docs/x.md", "x\nback again\n");

        var applied = await kit.FoldAsync("ag", apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("x\nback again\n", OrchestrationKit.Read(kit.Main, "docs/x.md"));
    }

    /// <summary>An untracked repository in the main tree is named, and never handed: a fold never moves a directory.</summary>
    [Fact]
    public async Task AnUntrackedRepositoryInTheMainTree_IsNamedAndNotHanded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var nested = Directory.CreateDirectory(Path.Combine(kit.Main, "nested")).FullName;
        await kit.Harness.InitializeGitRepositoryAsync(nested, TestContext.Current.CancellationToken);

        var created = await kit.Harness.AgentService.CreateAsync(kit.Main, "o1", "ag", "model-b", false, null, TestContext.Current.CancellationToken);

        Assert.True(created.Succeeded, OrchestrationKit.Describe(created));
        Assert.Contains(created.Details!, line => line.StartsWith("not handed: ", StringComparison.Ordinal) && line.Contains("nested", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(kit.Worktree("ag"), "nested")));
    }

    /// <summary>
    /// A path the main tree reaches through a link that stays inside it is never written through: the fold is refused. Settled
    /// by hand, it is left out, said not to be handed, and nothing is recorded for it.
    /// </summary>
    [Fact]
    public async Task APathTheMainTreeReachesThroughALinkInsideIt_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "docs/x.md", "x\nagent edit\n");
        Directory.Move(Path.Combine(kit.Main, "docs"), Path.Combine(kit.Main, "docs-real"));
        TestLinks.OrSkip(() => TestLinks.DirectoryLink(Path.Combine(kit.Main, "docs"), Path.Combine(kit.Main, "docs-real")));

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith("  'docs/x.md' is reached through a symbolic link or junction in the main tree", StringComparison.Ordinal));
        Assert.Equal("x\n", OrchestrationKit.Read(kit.Main, "docs-real/x.md"));

        var settled = await kit.FoldAsync("ag", apply: true, "docs/x.md");

        Assert.True(settled.Succeeded, OrchestrationKit.Describe(settled));
        Assert.Equal(
            "1 path(s) declared settled by hand that a fold never reads or writes, left out and not handed - the agent's next fold meets it again:",
            settled.Details![0]);
        Assert.StartsWith("  docs/x.md   (reached through a symbolic link or junction in the main tree, where it leads to '", settled.Details[1], StringComparison.Ordinal);
        Assert.Equal("x\n", OrchestrationKit.Read(kit.Main, "docs-real/x.md"));
        Assert.Equal("x\nagent edit\n", OrchestrationKit.Read(worktree, "docs/x.md"));
        Assert.DoesNotContain("docs/x.md", kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths.Keys);
    }

    /// <summary>
    /// A path the agent holds a repository of its own at, declared settled by hand, is left out and not handed as a link
    /// along it is: --settled stays the one way out of the refusal a directory otherwise meets, and nothing of the
    /// agent's is written or removed.
    /// </summary>
    [Fact]
    public async Task ASettledPathTheAgentHoldsARepositoryOfItsOwnAt_IsLeftOut_NeverRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        File.Delete(Path.Combine(worktree, "b.txt"));
        await kit.Harness.InitializeGitRepositoryAsync(Directory.CreateDirectory(Path.Combine(worktree, "b.txt")).FullName, TestContext.Current.CancellationToken);

        var settled = await kit.FoldAsync("ag", apply: true, "b.txt");

        Assert.True(settled.Succeeded, OrchestrationKit.Describe(settled));
        Assert.Equal(
            "1 path(s) declared settled by hand that a fold never reads or writes, left out and not handed - the agent's next fold meets it again:",
            settled.Details![0]);
        Assert.Equal("  b.txt   (a directory in the agent's worktree - a submodule, or a repository of its own)", settled.Details[1]);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.True(Directory.Exists(Path.Combine(worktree, "b.txt", ".git")));
        Assert.DoesNotContain("b.txt", kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths.Keys);
    }

    /// <summary>
    /// A settled path the main tree holds a submodule's entry at - an empty directory, which reads as no file there -
    /// or a repository of its own at is left out and not handed too, said as itself: nothing is reconciled by handing
    /// either, and the agent's copy stays its own.
    /// </summary>
    [Fact]
    public async Task ASettledPathTheMainTreeHoldsASubmodulesEntryOrARepositoryOfItsOwnAt_IsLeftOut()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "sub.txt", "one\n");
        OrchestrationKit.Write(kit.Main, "repo.txt", "two\n");
        await kit.Harness.CommitAllAsync(kit.Main, "two files the agent will change", TestContext.Current.CancellationToken);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "sub.txt", "one\nagent edit\n");
        OrchestrationKit.Write(worktree, "repo.txt", "two\nagent edit\n");
        var link = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", TestContext.Current.CancellationToken))!;
        File.Delete(Path.Combine(kit.Main, "sub.txt"));
        Directory.CreateDirectory(Path.Combine(kit.Main, "sub.txt"));
        await kit.GitAsync(kit.Main, "update-index", "--add", "--cacheinfo", $"160000,{link},sub.txt");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "a submodule where a file was");
        File.Delete(Path.Combine(kit.Main, "repo.txt"));
        await kit.Harness.InitializeGitRepositoryAsync(Directory.CreateDirectory(Path.Combine(kit.Main, "repo.txt")).FullName, TestContext.Current.CancellationToken);

        var settled = await kit.FoldAsync("ag", apply: true, "sub.txt", "repo.txt");

        Assert.True(settled.Succeeded, OrchestrationKit.Describe(settled));
        Assert.Equal(
            "2 path(s) declared settled by hand that a fold never reads or writes, left out and not handed - the agent's next fold meets them again:",
            settled.Details![0]);
        Assert.Equal("  repo.txt   (a directory in the main tree - a submodule, or a repository of its own)", settled.Details[1]);
        Assert.Equal("  sub.txt   (a directory in the main tree - a submodule, or a repository of its own)", settled.Details[2]);
        Assert.True(Directory.Exists(Path.Combine(kit.Main, "sub.txt")));
        Assert.True(Directory.Exists(Path.Combine(kit.Main, "repo.txt", ".git")));
        Assert.Equal("one\nagent edit\n", OrchestrationKit.Read(worktree, "sub.txt"));
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(worktree, "repo.txt"));
    }

    /// <summary>A rename the agent staged is folded as what it is: the new path written, and the old one removed.</summary>
    [Fact]
    public async Task ARenameTheAgentStaged_IsFoldedAsAWriteAndARemoval()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        await kit.GitAsync(worktree, "mv", "a.txt", "c.txt");

        var applied = await kit.FoldAsync("ag", apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("one\n", OrchestrationKit.Read(kit.Main, "c.txt"));
        Assert.False(File.Exists(Path.Combine(kit.Main, "a.txt")));
    }

    /// <summary>A path no record can keep - one another platform reads as rooted - is refused before anything is written.</summary>
    [Fact]
    public async Task APathNoRecordCanKeep_IsRefusedBeforeAnythingIsWritten()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows cannot hold a colon in a file name.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(worktree, "C:weird.txt", "rooted on Windows\n");

        var refused = await Assert.ThrowsAsync<HarnessException>(() => kit.FoldAsync("ag", apply: true));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("no record of an agent can keep a path another platform would read as somewhere else", refused.Message);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// A file the agent turned into a directory is folded as git would hold it - the file removed, then what the directory
    /// holds written - and so is a directory it turned into a file: what the directory held removed, with the directory,
    /// and the file written in its place. Folded, both are shared, and a fold run again has nothing of them to write.
    /// </summary>
    [Fact]
    public async Task AFileTheAgentTurnedIntoADirectory_AndADirectoryItTurnedIntoAFile_AreFolded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        File.Delete(Path.Combine(worktree, "b.txt"));
        OrchestrationKit.Write(worktree, "b.txt/inner.txt", "inner\n");
        Directory.Delete(Path.Combine(worktree, "docs"), recursive: true);
        OrchestrationKit.Write(worktree, "docs", "a file now\n");

        var applied = await kit.FoldAsync("ag", apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("inner\n", OrchestrationKit.Read(kit.Main, "b.txt/inner.txt"));
        Assert.Equal("a file now\n", OrchestrationKit.Read(kit.Main, "docs"));
        Assert.Contains("4 inherited path(s) left out; 0 path(s) are its own:", (await kit.FoldAsync("ag", apply: false)).Details!);
    }

    /// <summary>
    /// A file of the agent's where the main tree holds a directory of files the agent never deleted, and a file the agent made
    /// below a path the main tree holds as a file, each refuse the fold, named, before anything is written: written, the
    /// first would remove the main tree's files, and the second stop at its file part way - each file of the main tree's in
    /// the way said made since the agent's base, here by an uncommitted edit. A file of its base's the agent changed where
    /// the main tree made a directory of it is said to be one, once.
    /// </summary>
    [Fact]
    public async Task WhatTheMainTreeHoldsWhereTheAgentsFilesNeedRoom_RefusesTheFold()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var at = ReportText.Commit(kit.Record("ag").Base!);
        OrchestrationKit.Write(kit.Main, "notes/a.md", "the main tree's\n");
        OrchestrationKit.Write(worktree, "notes", "the agent's\n");
        OrchestrationKit.Write(kit.Main, "plan", "the main tree's plan\n");
        OrchestrationKit.Write(worktree, "plan/step.md", "the agent's step\n");
        File.Delete(Path.Combine(kit.Main, "a.txt"));
        OrchestrationKit.Write(kit.Main, "a.txt/inner.txt", "the main tree's\n");
        OrchestrationKit.Write(worktree, "a.txt", "one\nthe agent's\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            [
                "3 problem(s), and nothing was written:",
                $"  'a.txt' is at the agent's base {at}, and the main tree holds a directory there now, made by an uncommitted edit, so writing the agent's copy would lose that change",
                $"  'notes': the main tree holds a directory there with files the agent did not delete - notes/a.md (made since the agent's base {at} by an "
                    + "uncommitted edit) - which writing it would remove",
                $"  'plan/step.md' needs a directory at 'plan', where the main tree holds a file the agent did not delete, made since the agent's base {at} by an "
                    + "uncommitted edit",
            ],
            refused.Details!.Take(4));
        Assert.Equal("the main tree's\n", OrchestrationKit.Read(kit.Main, "notes/a.md"));
        Assert.Equal("the main tree's plan\n", OrchestrationKit.Read(kit.Main, "plan"));
        Assert.Equal("the main tree's\n", OrchestrationKit.Read(kit.Main, "a.txt/inner.txt"));
    }

    /// <summary>The real file system, except that one file is written anew just before its second read, as by an agent still at work.</summary>
    private sealed class ShiftingFileSystem(IFileSystem inner, string file, string later) : PassThroughFileSystem(inner)
    {
        private int _reads;

        public override Stream OpenRead(string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase) && Interlocked.Increment(ref _reads) == 2)
            {
                File.WriteAllText(file, later);
            }

            return base.OpenRead(path);
        }
    }

    /// <summary>The real file system, except that what is at one path cannot be asked, as under a directory this process may not look in.</summary>
    private sealed class UnaskableFileSystem(IFileSystem inner, string unaskable) : PassThroughFileSystem(inner)
    {
        public override Core.FileSystem.PathKind KindOf(string path)
            => string.Equals(Path.GetFullPath(path), Path.GetFullPath(unaskable), StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.")
                : base.KindOf(path);
    }
}
