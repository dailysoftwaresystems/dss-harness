using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Orchestration;
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
        Assert.Contains("  'a.txt': the main tree changed it after it was handed to the agent, so writing the agent's copy would lose that change", refused.Details!);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Equal("one\nmain edit again\n", OrchestrationKit.Read(kit.Main, "a.txt"));
    }

    /// <summary>
    /// A path the agent was not handed is measured against its own base, never the main tree's HEAD: a sibling's fold the
    /// main tree still holds as an edit, or holds committed, is never written over, and the refusal says which it was.
    /// </summary>
    [Theory]
    [InlineData(false, "by an uncommitted edit")]
    [InlineData(true, "by a commit")]
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
    /// --settled leaves a path reconciled by hand out, deletions included, which are asked about after it, so the rest of
    /// the agent's work goes in; the settled paths are named, and nothing is written for them.
    /// </summary>
    [Fact]
    public async Task SettledPaths_AreLeftOut_SoTheRestGoesIn_ADeletionAmongThem()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(worktree, "new.txt", "new\n");
        File.Delete(Path.Combine(worktree, "docs", "x.md"));
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmerged by hand\n");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nmain edit\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith("  'b.txt': the main tree changed it after the agent's base", StringComparison.Ordinal) && line.EndsWith("so writing the agent's copy would lose that change", StringComparison.Ordinal));
        Assert.Contains(refused.Details!, line => line.StartsWith("  'docs/x.md': the main tree changed it after the agent's base", StringComparison.Ordinal) && line.EndsWith("so removing it would lose that change", StringComparison.Ordinal));

        var applied = await kit.FoldAsync("ag", apply: true, "b.txt", "docs/x.md");

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains("2 path(s) declared settled by hand, neither compared nor written:", applied.Details!);
        Assert.Equal("two\nmerged by hand\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Equal("x\nmain edit\n", OrchestrationKit.Read(kit.Main, "docs/x.md"));
        Assert.Equal("new\n", OrchestrationKit.Read(kit.Main, "new.txt"));
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

    /// <summary>A path of the agent's that leads, in the main tree, through a link out of it is never written through.</summary>
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

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith("  'lib/f.txt' leads out of the main tree, through a link in the main tree", StringComparison.Ordinal));
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
        Assert.Contains(refused.Details!, line => line.StartsWith("  'tests/new.c' is not at the agent's base", StringComparison.Ordinal) && line.EndsWith("and the main tree holds it", StringComparison.Ordinal));
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
        Assert.Contains("  'a.txt': the main tree changed it after it was handed to the agent, so removing it would lose that change", refused.Details!);
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

    /// <summary>A path the main tree reaches through a link that stays inside it is never written through: the fold is refused.</summary>
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
