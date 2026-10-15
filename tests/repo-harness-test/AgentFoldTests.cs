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
    /// says what each did - by a commit alone only where the main tree's HEAD already holds what the main tree holds there
    /// now - since neither alone reconciles it: the edit after what the commit did where the edit made what the main tree
    /// holds of another kind than HEAD's, and the commit then the edit's change where the commit made it of that kind
    /// already. Every word of what a commit did is said by one of them, and the line under the refusals names the case of
    /// both.
    /// </summary>
    /// <param name="change">What a sibling's commit and then an uncommitted edit of the main tree did.</param>
    [Theory]
    [InlineData("removed")]
    [InlineData("removed-after-a-directory")]
    [InlineData("directory")]
    [InlineData("directory-after-a-change")]
    [InlineData("directory-then-changed")]
    [InlineData("changed")]
    [InlineData("changed-after-a-removal")]
    [InlineData("changed-after-a-directory")]
    [InlineData("made")]
    [InlineData("made-over-a-directory")]
    [InlineData("made-after-a-directory-removed")]
    [InlineData("made-after-a-directory-made")]
    public async Task ACommitAndAnUncommittedEditAfterIt_AreEachSaidWhatTheyDid(string change)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var at = ReportText.Commit(kit.Record("ag").Base!);
        var path = change switch
        {
            "made-over-a-directory" or "made-after-a-directory-removed" => "docs",
            "made-after-a-directory-made" => "notes",
            "made" => "new.txt",
            "changed" or "changed-after-a-removal" or "changed-after-a-directory" => "b.txt",
            _ => "a.txt",
        };

        if (path == "docs")
        {
            Directory.Delete(Path.Combine(worktree, "docs"), recursive: true);
        }

        OrchestrationKit.Write(worktree, path, "one\nthe agent's\n");

        // What a sibling's fold committed.
        switch (change)
        {
            case "directory" or "changed-after-a-removal":
                File.Delete(Path.Combine(kit.Main, path));
                break;
            case "removed-after-a-directory" or "directory-then-changed" or "changed-after-a-directory":
                File.Delete(Path.Combine(kit.Main, path));
                OrchestrationKit.Write(kit.Main, $"{path}/inner.txt", "the sibling's\n");
                break;
            case "made-over-a-directory":
                OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nthe sibling's\n");
                break;
            case "made-after-a-directory-removed":
                Directory.Delete(Path.Combine(kit.Main, "docs"), recursive: true);
                break;
            case "made-after-a-directory-made":
                OrchestrationKit.Write(kit.Main, "notes/a.md", "the sibling's\n");
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
            case "removed-after-a-directory":
                Directory.Delete(Path.Combine(kit.Main, path), recursive: true);
                break;
            case "directory" or "directory-after-a-change":
                File.Delete(Path.Combine(kit.Main, path));
                OrchestrationKit.Write(kit.Main, $"{path}/inner.txt", "the main tree's\n");
                break;
            case "directory-then-changed":
                OrchestrationKit.Write(kit.Main, $"{path}/more.txt", "the main tree's\n");
                break;
            case "changed-after-a-removal" or "made-after-a-directory-removed":
                OrchestrationKit.Write(kit.Main, path, "the main tree's\n");
                break;
            case "changed-after-a-directory" or "made-over-a-directory" or "made-after-a-directory-made":
                Directory.Delete(Path.Combine(kit.Main, path), recursive: true);
                OrchestrationKit.Write(kit.Main, path, "the main tree's\n");
                break;
            default:
                OrchestrationKit.Write(kit.Main, path, "the sibling's\nand the main tree's\n");
                break;
        }

        const string Lose = ", so writing the agent's copy would lose that change";
        var said = change switch
        {
            "removed" => $"  'a.txt' is at the agent's base {at}, and missing from the main tree, removed by an uncommitted edit, after a commit changed it{Lose}",
            "removed-after-a-directory" => $"  'a.txt' is at the agent's base {at}, and missing from the main tree, removed by an uncommitted edit, after a commit made a "
                + $"directory of it{Lose}",
            "directory" => $"  'a.txt' is at the agent's base {at}, and the main tree holds a directory there now, made by an uncommitted edit, after a commit "
                + $"removed it{Lose}",
            "directory-after-a-change" => $"  'a.txt' is at the agent's base {at}, and the main tree holds a directory there now, made by an uncommitted edit, after a "
                + $"commit changed it{Lose}",
            "directory-then-changed" => $"  'a.txt' is at the agent's base {at}, and the main tree holds a directory there now, made by a commit, then changed by an "
                + $"uncommitted edit{Lose}",
            "changed" => $"  'b.txt': the main tree changed it after the agent's base {at}, by a commit, then changed by an uncommitted edit{Lose}",
            "changed-after-a-removal" => $"  'b.txt': the main tree changed it after the agent's base {at}, by an uncommitted edit, after a commit removed it{Lose}",
            "changed-after-a-directory" => $"  'b.txt': the main tree changed it after the agent's base {at}, by an uncommitted edit, after a commit made a directory "
                + $"of it{Lose}",
            "made" => $"  'new.txt' is not at the agent's base {at}, and the main tree made it since, by a commit, then changed by an uncommitted edit{Lose}",
            "made-over-a-directory" => $"  'docs' is not at the agent's base {at}, and the main tree made it since, by an uncommitted edit, after a commit changed the "
                + $"directory there{Lose}",
            "made-after-a-directory-removed" => $"  'docs' is not at the agent's base {at}, and the main tree made it since, by an uncommitted edit, after a commit "
                + $"removed the directory there{Lose}",
            _ => $"  'notes' is not at the agent's base {at}, and the main tree made it since, by an uncommitted edit, after a commit made a directory there{Lose}",
        };

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith(said, StringComparison.Ordinal));
        Assert.Contains(
            refused.Details!,
            line => line.StartsWith(
                "A path the main tree changed is usually two agents on one file: by an uncommitted edit while the other's fold is in the tree, by a commit "
                    + "once it was committed, and by both where a committed change was edited again.",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// A rename a commit made of a path's case alone, where the repository ignores case, is what that commit did: a main
    /// tree that holds what its HEAD holds says the path changed by a commit, one that edited the renamed file since says
    /// the commit changed it and the edit then changed it again, and one that removed it says it removed it after a commit
    /// renamed it - git asked about the main tree's file as it spells it, in its HEAD and in its status.
    /// </summary>
    /// <param name="since">What the main tree did to the renamed file since, uncommitted: nothing, edited it, or removed it.</param>
    [Theory]
    [InlineData("nothing")]
    [InlineData("edited")]
    [InlineData("removed")]
    public async Task ARenameOfCaseAloneACommitMade_IsSaidAsThatCommitsOwn(string since)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var ignoresCase = (await kit.Harness.GitClient.RunAsync(kit.Main, ["config", "--type=bool", "--default=false", "core.ignorecase"], cancellationToken: TestContext.Current.CancellationToken))
            .StandardOutput.Trim() == "true";
        Assert.SkipUnless(ignoresCase, "Only a repository that ignores case holds a rename of case alone as the same file.");
        var worktree = await kit.CreateAgentAsync("ag");
        var at = ReportText.Commit(kit.Record("ag").Base!);
        OrchestrationKit.Write(worktree, "a.txt", "one\nthe agent's\n");
        await kit.GitAsync(kit.Main, "mv", "a.txt", "A.txt");
        await kit.Harness.CommitAllAsync(kit.Main, "a sibling's rename", TestContext.Current.CancellationToken);

        switch (since)
        {
            case "edited":
                OrchestrationKit.Write(kit.Main, "A.txt", "one\nthe main tree's\n");
                break;
            case "removed":
                File.Delete(Path.Combine(kit.Main, "A.txt"));
                break;
        }

        var said = since switch
        {
            "edited" => $"  'a.txt': the main tree changed it after the agent's base {at}, by a commit, then changed by an uncommitted edit, so writing the agent's "
                + "copy would lose that change",
            "removed" => $"  'a.txt' is at the agent's base {at}, and missing from the main tree, removed by an uncommitted edit, after a commit renamed it, so "
                + "writing the agent's copy would lose that change",
            _ => $"  'a.txt': the main tree changed it after the agent's base {at}, by a commit, so writing the agent's copy would lose that change",
        };

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith(said, StringComparison.Ordinal));
    }

    /// <summary>
    /// A rename of case alone the agent made, where the trees find a name in any case, is refused by name - its dry run, its
    /// --apply and its deletion alike, with an edit or without - and neither name is taken for one the main tree already
    /// holds: written there, the file would go on under the name the main tree's git has. Once the git mv -f it names makes
    /// the rename there, each name is read only where a tree spells it so, and the fold goes through, the old name recorded
    /// as absent from both; an edit besides is weighed against the base's file under its old name - what the uncommitted git
    /// mv did, never a commit's, and never a file both trees made - so its clean merge is offered, and --merge folds it.
    /// All of it alike where the repository does not say it ignores case: the file system finds the two names as one file,
    /// and the base's file is found in any case, whatever core.ignorecase says.
    /// </summary>
    /// <param name="edited">Whether the agent edited the renamed file too.</param>
    /// <param name="ignoresCase">Whether the repository says it ignores case.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task ARenameOfCaseAloneTheAgentMade_IsRefusedByName_UntilTheMainTreeMakesIt(bool edited, bool ignoresCase)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        await CaseFoldedAsync(kit, ignoresCase);
        var at = ReportText.Commit(kit.Record("ag").Base!);
        await kit.GitAsync(worktree, "mv", "-f", "a.txt", "A.txt");

        if (edited)
        {
            OrchestrationKit.Write(worktree, "A.txt", "one\nthe agent's\n");
        }

        foreach (var refused in new[] { await kit.FoldAsync("ag", apply: false), await kit.FoldAsync("ag", apply: true), await kit.DeleteAsync("ag", apply: true) })
        {
            Assert.Equal(HarnessExit.Refused, refused.ExitCode);
            Assert.Contains(RenamedInCase("a.txt", "A.txt"), refused.Details!);
            Assert.DoesNotContain(refused.Details!, line => line.StartsWith("  'a.txt'", StringComparison.Ordinal) || line.Contains("already holds", StringComparison.Ordinal));
        }

        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
        Assert.Equal(["a.txt"], Directory.EnumerateFiles(kit.Main, "?.txt").Select(Path.GetFileName).Where(name => string.Equals(name, "a.txt", StringComparison.OrdinalIgnoreCase)));

        await kit.GitAsync(kit.Main, "mv", "-f", "a.txt", "A.txt");
        var folded = await kit.FoldAsync("ag", apply: true);

        if (edited)
        {
            Assert.Equal(HarnessExit.Refused, folded.ExitCode);
            Assert.Contains(
                $"  'A.txt': the main tree changed it after the agent's base {at}, by an uncommitted edit, so writing the agent's copy would lose that change; a three-way "
                    + "merge of the two is clean - 1 hunk(s) from the agent, 0 from the main tree - and --merge A.txt writes it",
                folded.Details!);

            folded = await kit.FoldAsync("ag", apply: true, new FoldAllowances { Merge = ["A.txt"] });
        }

        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Equal(edited ? "one\nthe agent's\n" : "one\n", OrchestrationKit.Read(kit.Main, "A.txt"));
        Assert.Contains("a.txt", seed.Absent ?? []);
        Assert.Contains("A.txt", seed.Paths.Keys);
        Assert.DoesNotContain("a.txt", seed.Paths.Keys);
    }

    /// <summary>
    /// A directory the agent renamed in case alone - its folder renamed on disk, or its files renamed in its git index
    /// alone, the folder keeping its name - is refused file by file, each naming the git mv -f that makes the rename in the
    /// main tree: run, it renames what the main tree's git holds, though its folder keeps its name on disk, and the fold
    /// run again goes through, each old name recorded as absent from both.
    /// </summary>
    /// <param name="inTheIndexAlone">Whether the agent renamed the files in its git index alone.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADirectoryTheAgentRenamedInCase_IsRefusedByName_AndTheGitMvItNamesLetsTheFoldThrough(bool inTheIndexAlone)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        Assert.SkipUnless(await kit.Harness.GitClient.IgnoresCaseAsync(kit.Main, TestContext.Current.CancellationToken), "Only a repository that ignores case holds a rename of case alone as the same file.");
        var worktree = await kit.CreateAgentAsync("ag");

        if (inTheIndexAlone)
        {
            await kit.GitAsync(worktree, "mv", "-f", "docs/x.md", "Docs/x.md");
        }
        else
        {
            await kit.GitAsync(worktree, "mv", "docs", "renaming");
            await kit.GitAsync(worktree, "mv", "renaming", "Docs");
        }

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(RenamedInCase("docs/x.md", "Docs/x.md"), refused.Details!);

        await kit.GitAsync(kit.Main, "mv", "-f", "docs/x.md", "Docs/x.md");
        var folded = await kit.FoldAsync("ag", apply: true);
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Contains("docs/x.md", seed.Absent ?? []);
        Assert.Contains("Docs/x.md", seed.Paths.Keys);
    }

    /// <summary>
    /// A rename of case alone the agent made of a file it then deleted is no rename to refuse: the agent holds no file under
    /// either name, so the fold removes the file from the main tree, as it does any deletion of the agent's.
    /// </summary>
    [Fact]
    public async Task ARenameOfCaseAloneTheAgentMade_OfAFileItThenDeleted_FoldsAsItsDeletion()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        Assert.SkipUnless(await kit.Harness.GitClient.IgnoresCaseAsync(kit.Main, TestContext.Current.CancellationToken), "Only a repository that ignores case holds a rename of case alone as the same file.");
        var worktree = await kit.CreateAgentAsync("ag");
        await kit.GitAsync(worktree, "mv", "a.txt", "A.txt");
        File.Delete(Path.Combine(worktree, "A.txt"));

        var folded = await kit.FoldAsync("ag", apply: true);

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.False(File.Exists(Path.Combine(kit.Main, "a.txt")), "The main tree holds the file the agent deleted under either name.");
    }

    /// <summary>
    /// A rename of case alone the agent made of a file the main tree removed since is weighed against the base's file under
    /// its old name, found in any case whether the repository says it ignores case or not: the removal is said and refuses
    /// the fold, never undone by writing the agent's file under its new name, and --settled hands the agent the removal.
    /// </summary>
    /// <param name="edited">Whether the agent edited the renamed file too.</param>
    /// <param name="ignoresCase">Whether the repository says it ignores case.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task ARenameOfCaseAloneTheAgentMade_OfAFileTheMainTreeRemoved_RefusesTheFoldAsTheRemoval(bool edited, bool ignoresCase)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        await CaseFoldedAsync(kit, ignoresCase);
        var at = ReportText.Commit(kit.Record("ag").Base!);
        await kit.GitAsync(worktree, "mv", "-f", "a.txt", "A.txt");
        File.Delete(Path.Combine(kit.Main, "a.txt"));

        if (edited)
        {
            OrchestrationKit.Write(worktree, "A.txt", "one\nthe agent's\n");
        }

        foreach (var refused in new[] { await kit.FoldAsync("ag", apply: false), await kit.FoldAsync("ag", apply: true) })
        {
            Assert.Equal(HarnessExit.Refused, refused.ExitCode);
            Assert.Contains(
                $"  'A.txt' is at the agent's base {at}, and missing from the main tree, removed by an uncommitted edit, so writing the agent's copy would lose that change",
                refused.Details!);
        }

        Assert.False(File.Exists(Path.Combine(kit.Main, "a.txt")), "The main tree holds the file it removed under either name.");

        var settled = await kit.FoldAsync("ag", apply: true, "A.txt");

        Assert.True(settled.Succeeded, OrchestrationKit.Describe(settled));
        Assert.False(File.Exists(Path.Combine(worktree, "A.txt")));
        Assert.False(File.Exists(Path.Combine(kit.Main, "a.txt")), "The main tree holds the file it removed under either name.");
    }

    /// <summary>
    /// Skips unless the main tree's file system finds a name in any case, and the repository says it ignores case where
    /// <paramref name="ignoresCase"/>; otherwise makes it say it does not, for the main tree and the agent's worktree alike.
    /// </summary>
    /// <param name="kit">The repository and its agent.</param>
    /// <param name="ignoresCase">Whether the repository is to say it ignores case.</param>
    internal static async Task CaseFoldedAsync(OrchestrationKit kit, bool ignoresCase)
    {
        Assert.SkipUnless(PathCase.In(kit.Harness.FileSystem, kit.Main).Equals("a", "A"), "Only a file system that finds a name in any case holds a rename of case alone as the same file.");

        if (ignoresCase)
        {
            Assert.SkipUnless(await kit.Harness.GitClient.IgnoresCaseAsync(kit.Main, TestContext.Current.CancellationToken), "The repository does not say it ignores case.");
        }
        else
        {
            await kit.GitAsync(kit.Main, "config", "core.ignorecase", "false");
        }
    }

    /// <summary>The refusal of a rename of case alone the agent made of <paramref name="named"/> to <paramref name="renamed"/>, as a line of the fold's.</summary>
    private static string RenamedInCase(string named, string renamed)
        => $"  '{renamed}': the agent renamed '{named}' to it in case alone, which a fold cannot write - the main tree finds a name in any case, and its git would go on "
            + $"naming the file '{named}': run git mv -f '{named}' '{renamed}' in the main tree, then fold again";

    /// <summary>
    /// A file the main tree holds where neither its HEAD nor the agent's base holds one, and that git ignores, is said to be
    /// one git ignores, with no word of when it came: git never recorded it, and it may well be older than the base.
    /// </summary>
    [Fact]
    public async Task AFileGitIgnoresWhereNeitherHeadNorTheBaseHoldsOne_IsSaidIgnored_NotMadeSince()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var at = ReportText.Commit(kit.Record("ag").Base!);
        File.AppendAllText(Path.Combine(kit.Main, ".gitignore"), "*.log\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a sibling's fold ignores logs", TestContext.Current.CancellationToken);
        OrchestrationKit.Write(kit.Main, "debug.log", "the main tree's\n");
        OrchestrationKit.Write(worktree, "debug.log", "the agent's\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            refused.Details!,
            line => line.StartsWith(
                $"  'debug.log' is not at the agent's base {at}, and the main tree holds a file there that git ignores, so writing the agent's copy would lose "
                    + "that file",
                StringComparison.Ordinal));
        Assert.DoesNotContain(refused.Details!, line => line.StartsWith("  'debug.log'", StringComparison.Ordinal) && line.Contains("since", StringComparison.Ordinal));
    }

    /// <summary>
    /// A file of a repository nested in the main tree, untracked, is said made since the agent's base by an uncommitted
    /// edit, never ignored: git status folds the repository into one entry above the file and looks no further inside, so
    /// a rule of the main tree's that matches the file says nothing of it.
    /// </summary>
    /// <param name="ruleMatches">Whether a rule of the main tree's .gitignore matches the file.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFileOfARepositoryNestedInTheMainTree_IsSaidMadeByAnUncommittedEdit_NeverIgnored(bool ruleMatches)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var at = ReportText.Commit(kit.Record("ag").Base!);

        if (ruleMatches)
        {
            File.AppendAllText(Path.Combine(kit.Main, ".gitignore"), "*.c\n");
            await kit.Harness.CommitAllAsync(kit.Main, "a sibling's fold ignores objects' sources", TestContext.Current.CancellationToken);
        }

        Directory.CreateDirectory(Path.Combine(kit.Main, "vendor", "lib"));
        await kit.GitAsync(Path.Combine(kit.Main, "vendor", "lib"), "init", "--quiet");
        OrchestrationKit.Write(kit.Main, "vendor/lib/lib.c", "the clone's\n");
        OrchestrationKit.Write(worktree, "vendor/lib/lib.c", "the agent's\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            refused.Details!,
            line => line.StartsWith(
                $"  'vendor/lib/lib.c' is not at the agent's base {at}, and the main tree made it since, by an uncommitted edit, so writing the agent's "
                    + "copy would lose that change",
                StringComparison.Ordinal));
        Assert.DoesNotContain(refused.Details!, line => line.Contains("ignore", StringComparison.Ordinal));
    }

    /// <summary>
    /// The files of a repository nested in a directory of the main tree's that a file of the agent's replaces - its .git
    /// among them - are each said made since the agent's base by an uncommitted edit, the base named once, and none
    /// ignored: git status folds the repository into one entry above them.
    /// </summary>
    [Fact]
    public async Task FilesOfARepositoryNestedInADirectoryInTheWay_AreSaidMadeByAnUncommittedEdit_NoneIgnored()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var at = ReportText.Commit(kit.Record("ag").Base!);
        Directory.CreateDirectory(Path.Combine(kit.Main, "notes", "sub"));
        await kit.GitAsync(Path.Combine(kit.Main, "notes", "sub"), "init", "--quiet");
        OrchestrationKit.Write(kit.Main, "notes/sub/lib.c", "the clone's\n");
        OrchestrationKit.Write(worktree, "notes", "the agent's\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        var line = Assert.Single(
            refused.Details!,
            line => line.StartsWith("  'notes': the main tree holds a directory there with files the fold does not remove - ", StringComparison.Ordinal));
        Assert.DoesNotContain("ignore", line, StringComparison.Ordinal);
        Assert.StartsWith(
            "  'notes': the main tree holds a directory there with files the fold does not remove - notes/sub/.git/",
            line,
            StringComparison.Ordinal);
        Assert.Matches(
            $@" - notes/sub/\.git/\S+ \(made since the agent's base {at} by an uncommitted edit\), notes/sub/\.git/\S+ \(made since then by an uncommitted edit\), ",
            line);
        Assert.Equal(2, line.Split($"since the agent's base {at}").Length);
    }

    /// <summary>
    /// A path in a submodule of the main tree's is never written into it, whatever the submodule holds there - nothing, no
    /// checkout at all, or a file of its own: the fold is refused, exit 13, saying the path lies in a submodule the main
    /// tree made by a commit or staged by an uncommitted edit, and nothing is written there. git never asks the submodule
    /// about the path, and check-ignore refuses it.
    /// </summary>
    /// <param name="committed">Whether the main tree committed the submodule, rather than only staged it.</param>
    /// <param name="there">What the submodule holds at the path: nothing, no checkout at all, or a file.</param>
    [Theory]
    [InlineData(false, "nothing")]
    [InlineData(true, "nothing")]
    [InlineData(false, "no checkout")]
    [InlineData(true, "no checkout")]
    [InlineData(false, "a file")]
    [InlineData(true, "a file")]
    public async Task APathInASubmoduleOfTheMainTree_IsRefused_NeverWrittenIntoIt(bool committed, string there)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var nested = Path.Combine(kit.Main, "vendor", "lib");
        await MakeSubmoduleAsync(kit, nested, there == "a file" ? "the clone's\n" : null, committed);

        if (there == "no checkout")
        {
            RemoveTree(nested);
        }

        OrchestrationKit.Write(worktree, "vendor/lib/lib.c", "the agent's\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            $"  'vendor/lib/lib.c' lies in 'vendor/lib', a submodule of the main tree's {(committed ? "made by a commit" : "staged by an uncommitted edit")} - a "
                + "repository of its own, which a fold never writes into or removes from",
            refused.Details!);
        Assert.Equal(there == "a file" ? "the clone's\n" : null, File.Exists(Path.Combine(nested, "lib.c")) ? OrchestrationKit.Read(kit.Main, "vendor/lib/lib.c") : null);
    }

    /// <summary>
    /// A file of the agent's where the main tree records a submodule, its checkout gone, is never written there: the fold
    /// is refused, exit 13, saying the path is a submodule of the main tree's, and nothing is written.
    /// </summary>
    [Fact]
    public async Task AFileWhereTheMainTreeRecordsASubmodule_IsRefused_NeverWritten()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var nested = Path.Combine(kit.Main, "vendor", "lib");
        await MakeSubmoduleAsync(kit, nested, file: null, committed: true);
        RemoveTree(nested);
        OrchestrationKit.Write(worktree, "vendor/lib", "the agent's\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            "  'vendor/lib' is a submodule of the main tree's made by a commit - a repository of its own, which a fold never writes into or removes from",
            refused.Details!);
        Assert.False(File.Exists(nested));
    }

    /// <summary>
    /// A path in a submodule of the main tree's settled by hand is left out, never handed nor written: a fold never writes
    /// into another repository, and --settled is not the way into one.
    /// </summary>
    [Fact]
    public async Task APathInASubmoduleOfTheMainTree_SettledByHand_IsLeftOut_NeverWritten()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var nested = Path.Combine(kit.Main, "vendor", "lib");
        await MakeSubmoduleAsync(kit, nested, file: null, committed: true);
        OrchestrationKit.Write(worktree, "vendor/lib/lib.c", "the agent's\n");

        var folded = await kit.FoldAsync("ag", apply: true, "vendor/lib/lib.c");

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Contains("  vendor/lib/lib.c   (in 'vendor/lib', a submodule of the main tree's - a repository of its own)", folded.Details!);
        Assert.False(File.Exists(Path.Combine(nested, "lib.c")));
        Assert.Equal("the agent's\n", OrchestrationKit.Read(worktree, "vendor/lib/lib.c"));
    }

    /// <summary>
    /// The files of a repository the main tree staged as a submodule, in a directory a file of the agent's replaces, are
    /// each said made since the agent's base by an uncommitted edit, as the staging was, the base named once - and the
    /// fold is refused, never failed.
    /// </summary>
    [Fact]
    public async Task FilesOfASubmoduleInADirectoryInTheWay_AreSaidMadeByAnUncommittedEdit_AndTheFoldIsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var at = ReportText.Commit(kit.Record("ag").Base!);
        var nested = Path.Combine(kit.Main, "notes", "sub");
        Directory.CreateDirectory(nested);
        await kit.Harness.InitializeGitRepositoryAsync(nested, TestContext.Current.CancellationToken);
        await kit.GitAsync(kit.Main, "add", "notes/sub");
        OrchestrationKit.Write(worktree, "notes", "the agent's\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        var line = Assert.Single(
            refused.Details!,
            line => line.StartsWith("  'notes': the main tree holds a directory there with files the fold does not remove - ", StringComparison.Ordinal));
        Assert.DoesNotContain("ignore", line, StringComparison.Ordinal);
        Assert.Matches(
            $@" - notes/sub/\S+ \(made since the agent's base {at} by an uncommitted edit\), notes/sub/\S+ \(made since then by an uncommitted edit\), ",
            line);
        Assert.Equal(2, line.Split($"since the agent's base {at}").Length);
    }

    /// <summary>
    /// What the main tree holds where git status lists nothing, and HEAD holds nothing, is said ignored only where git
    /// says it ignores it: an empty directory made where a commit removed the base's file is listed by nothing, and is no
    /// file git ignores - said made by an uncommitted edit after that commit.
    /// </summary>
    [Fact]
    public async Task AnEmptyDirectoryMadeWhereACommitRemovedTheBasesFile_IsSaidMadeByAnUncommittedEdit_NotIgnored()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var at = ReportText.Commit(kit.Record("ag").Base!);
        OrchestrationKit.Write(worktree, "a.txt", "one\nthe agent's\n");
        File.Delete(Path.Combine(kit.Main, "a.txt"));
        await kit.Harness.CommitAllAsync(kit.Main, "a sibling's removal", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(kit.Main, "a.txt"));

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            refused.Details!,
            line => line.StartsWith(
                $"  'a.txt' is at the agent's base {at}, and the main tree holds a directory there now, made by an uncommitted edit, after a commit "
                    + "removed it, so writing the agent's copy would lose that change",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// A directory a commit made where the base holds a file, spelled in another case, is that commit's, where the
    /// repository ignores case: HEAD's entry is found as HEAD spells the directory.
    /// </summary>
    [Fact]
    public async Task ADirectoryACommitMadeInAnotherCaseWhereTheBaseHeldAFile_IsSaidMadeByThatCommit()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var ignoresCase = (await kit.Harness.GitClient.RunAsync(kit.Main, ["config", "--type=bool", "--default=false", "core.ignorecase"], cancellationToken: TestContext.Current.CancellationToken))
            .StandardOutput.Trim() == "true";
        Assert.SkipUnless(ignoresCase, "Only a repository that ignores case holds a directory spelled in another case at the same path.");
        var worktree = await kit.CreateAgentAsync("ag");
        var at = ReportText.Commit(kit.Record("ag").Base!);
        OrchestrationKit.Write(worktree, "a.txt", "one\nthe agent's\n");
        File.Delete(Path.Combine(kit.Main, "a.txt"));
        OrchestrationKit.Write(kit.Main, "A.TXT/inner.txt", "the sibling's\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a sibling's directory", TestContext.Current.CancellationToken);

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            refused.Details!,
            line => line.StartsWith(
                $"  'a.txt' is at the agent's base {at}, and the main tree holds a directory there now, made by a commit, so writing the agent's copy "
                    + "would lose that change",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// What the main tree holds in the way of the agent's files is said made since the agent's base by a commit or by an
    /// uncommitted edit, as any other path the main tree moved since is: a file where one of the agent's files needs a
    /// directory, and each file of a directory one of them replaces - each a file the fold does not remove.
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
            ? $"  'notes': the main tree holds a directory there with files the fold does not remove - notes/a.md (made since the agent's base {at} {how}) - "
                + "which writing it would remove"
            : $"  'plan/step.md' needs a directory at 'plan', where the main tree holds a file the fold does not remove, made since the agent's base {at} {how}";

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(said, refused.Details!);
    }

    /// <summary>
    /// Files in the way of one of the agent's are listed each with what it is, the agent's base named once - the first said
    /// made since it, the next since then - and a file git ignores is said to be one, with no word of when it came; a file
    /// a commit made and an uncommitted edit changed since is said made by the one and changed by the other.
    /// </summary>
    [Fact]
    public async Task FilesInTheWay_AreListedEachAsItCame_TheBaseNamedOnce()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        File.AppendAllText(Path.Combine(kit.Main, ".gitignore"), "*.log\n");
        await kit.Harness.CommitAllAsync(kit.Main, "logs are ignored", TestContext.Current.CancellationToken);
        var worktree = await kit.CreateAgentAsync("ag");
        var at = ReportText.Commit(kit.Record("ag").Base!);
        OrchestrationKit.Write(kit.Main, "notes/a.md", "the sibling's\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a sibling's fold", TestContext.Current.CancellationToken);
        OrchestrationKit.Write(kit.Main, "notes/a.md", "the sibling's\nand the main tree's\n");
        OrchestrationKit.Write(kit.Main, "notes/b.md", "the main tree's\n");
        OrchestrationKit.Write(kit.Main, "notes/x.log", "the main tree's log\n");
        OrchestrationKit.Write(worktree, "notes", "the agent's\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            $"  'notes': the main tree holds a directory there with files the fold does not remove - notes/a.md (made since the agent's base {at} by a "
                + "commit, then changed by an uncommitted edit), notes/b.md (made since then by an uncommitted edit), notes/x.log (ignored by git) - which "
                + "writing it would remove",
            refused.Details!);
    }

    /// <summary>
    /// One fold asks git what the main tree holds against its HEAD once, however many of its refusals need the answer: a
    /// path the main tree moved since the agent's base and a file in the way of the agent's.
    /// </summary>
    [Fact]
    public async Task OneFold_AsksWhatTheMainTreeHoldsAgainstItsHeadOnce()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nthe agent's\n");
        OrchestrationKit.Write(worktree, "plan/step.md", "the agent's\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nthe sibling's\n");
        OrchestrationKit.Write(kit.Main, "plan", "the sibling's\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a sibling's fold", TestContext.Current.CancellationToken);
        var main = Path.GetFullPath(kit.Main).TrimEnd(Path.DirectorySeparatorChar);
        var asked = 0;
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            BeforeReadStatus = directory => asked += string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), main, StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            BeforeListChangedSince = (_, commit) => asked += commit == "HEAD" ? 1 : 0,
        };
        var agents = kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git);

        var refused = await agents.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, "ag", new FoldAllowances(), apply: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith("  'b.txt': the main tree changed it after the agent's base", StringComparison.Ordinal));
        Assert.Contains(refused.Details!, line => line.StartsWith("  'plan/step.md' needs a directory at 'plan'", StringComparison.Ordinal));
        Assert.Equal(1, asked);
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
            "  'a.txt/step.md' needs a directory at 'a.txt', where the main tree holds a file the fold does not remove"
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
            "where what the main tree holds is in the way of what it writes - a file where a directory goes, or files the fold does "
                + "not remove in a directory a file replaces, each said made or changed since the agent's base where the main tree "
                + "did either, as any other path is, or ignored by git - and for an anchor registry",
            help,
            StringComparison.Ordinal);

        Assert.Contains(
            "for a link, a repository of its own, a path in a submodule of the main tree's - which the message says it made by a commit or "
                + "staged, and which --settled leaves out - or a path leading out of the main tree",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            "A path in a submodule the agent's worktree records - where the main tree turned one into files of its own - refuses "
                + "create-agent before its worktree is made, and seed-agent and refresh-agent, writing nothing: a hand-over never writes "
                + "into another repository, and rebase-agent brings in what the main tree committed there.",
            help,
            StringComparison.Ordinal);

        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "a path in a submodule of the main tree's - at or below a gitlink its index holds, said made by a commit or staged by an "
                + "uncommitted edit, and left out where settled by hand, since nothing a fold wrote there could be committed in the main tree -",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "a path in a submodule the agent's worktree records - at or below a gitlink its index holds, or for a worktree not made yet "
                + "the main tree's HEAD, where the main tree turned one into files of its own - refuses create-agent before the worktree is "
                + "made, and seed-agent and refresh-agent before anything is written (`AgentFold.InSubmodulesAsync`): a hand-over never "
                + "writes into another repository, and rebase-agent, whose move takes the index along, brings in what the main tree "
                + "committed there.",
            architecture,
            StringComparison.Ordinal);

        Assert.Contains(
            "A refusal of a main-tree path the agent shares nothing for, made, changed, made a directory of or removed since the "
                + "agent's base, says whether a commit, an uncommitted edit or a commit and then an edit did it, since each is "
                + "reconciled differently: a commit alone where the main tree's HEAD already holds what the main tree holds there now, "
                + "which `git status` - untracked files listed, its paths compared as `core.ignorecase` says, so a rename of case alone "
                + "is the commit's - answers by listing nothing at the path, below it, or in a directory above it that it folds into one "
                + "entry, as it folds an untracked repository nested in the tree (one process a fold, asked only where a commit moved a "
                + "path or neither HEAD nor the base holds one); an uncommitted edit alone where HEAD holds what the base holds, each entry "
                + "compared as git compares a commit, so a commit of a mode alone is a commit; and both otherwise, "
                + "each said with what it did - the edit after what the commit did where the edit made what the main tree holds of "
                + "another kind than HEAD's, and the commit then the edit's change where the commit already made it of that kind.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "A file of the main tree in the way of the agent's - where one of its files needs a directory, or in a directory one "
                + "replaces - is a file the fold does not remove, said made or changed since the base where the main tree did "
                + "either, the same way, and a file git ignores where neither HEAD nor the base holds one - as `git check-ignore` says, "
                + "asked only of what status lists nothing at where HEAD holds nothing and no submodule holds the path, never read from "
                + "what status leaves out - is said ignored by git, with no word of when it came. A file of a submodule of the main "
                + "tree's in the way - below a gitlink its index holds, where git looks at nothing and `check-ignore` answers nothing - came as "
                + "that record did: by a commit where HEAD holds it, then changed by an uncommitted edit where status lists the "
                + "record, and by an uncommitted edit where only the index holds it "
                + "(`Came`, the one wording of how a path came to be held, and `Since`, of what the main tree did, both decided once).",
            architecture,
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

    /// <summary>
    /// A settled deletion is guarded as a settled copy is: a file the main tree removed, reconciled by hand, that the agent's
    /// session rewrites after it was weighed is never removed, and the fold stops there, exit 21, naming it.
    /// </summary>
    [Fact]
    public async Task ASettledDeletion_StopsWhereTheAgentsFileChangedSinceItWasWeighed()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        File.Delete(Path.Combine(kit.Main, "b.txt"));
        var copy = Path.Combine(worktree, "b.txt");

        // Its third reading is the one that asks, before the removal, whether it is what was weighed: the session writes first.
        var agents = kit.Harness.Agents(
            new AgentFoldMergeTests.MeanwhileFileSystem(kit.Harness.FileSystem, copy, 3, () => File.WriteAllText(copy, "two\nthe session's edit\n")),
            kit.Harness.AnchorRegistryService);

        var stopped = await agents.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances { Settled = ["b.txt"] }, apply: true, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith(
            "Folding agent 'ag' of 'o1' stopped part way, after writing 0 of 0 path(s) and removing 0 of 0, then handing the agent 0 of 1 settled by hand: "
                + "'b.txt' holds other content in the agent's worktree now than when it was measured, so it was not removed.",
            stopped.Message,
            StringComparison.Ordinal);
        Assert.Equal("two\nthe session's edit\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.DoesNotContain("b.txt", kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Absent ?? []);
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
                $"  'notes': the main tree holds a directory there with files the fold does not remove - notes/a.md (made since the agent's base {at} by an "
                    + "uncommitted edit) - which writing it would remove",
                $"  'plan/step.md' needs a directory at 'plan', where the main tree holds a file the fold does not remove, made since the agent's base {at} by an "
                    + "uncommitted edit",
            ],
            refused.Details!.Take(4));
        Assert.Equal("the main tree's\n", OrchestrationKit.Read(kit.Main, "notes/a.md"));
        Assert.Equal("the main tree's plan\n", OrchestrationKit.Read(kit.Main, "plan"));
        Assert.Equal("the main tree's\n", OrchestrationKit.Read(kit.Main, "a.txt/inner.txt"));
    }

    /// <summary>The real file system, except that one file is written anew just before its second read, as by an agent still at work.</summary>
    /// <summary>
    /// Makes a repository of its own at <paramref name="nested"/>, holding lib.c where <paramref name="file"/> is given,
    /// and records it as a submodule of the main tree's: staged, and committed where <paramref name="committed"/>.
    /// </summary>
    internal static async Task MakeSubmoduleAsync(OrchestrationKit kit, string nested, string? file, bool committed)
    {
        Directory.CreateDirectory(nested);
        await kit.Harness.InitializeGitRepositoryAsync(nested, TestContext.Current.CancellationToken);

        if (file is not null)
        {
            File.WriteAllText(Path.Combine(nested, "lib.c"), file);
            await kit.Harness.CommitAllAsync(nested, "the clone's", TestContext.Current.CancellationToken);
        }

        await kit.GitAsync(kit.Main, "add", Path.GetRelativePath(kit.Main, nested).Replace(Path.DirectorySeparatorChar, '/'));

        if (committed)
        {
            await kit.Harness.CommitAllAsync(kit.Main, "a sibling's submodule", TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Removes <paramref name="directory"/> and everything in it, git's read-only objects among them.</summary>
    internal static void RemoveTree(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }

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
