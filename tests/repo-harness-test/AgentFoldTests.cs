using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// fold-agent: an agent's own work measured - told apart from what it was handed and from what the main tree did
/// meanwhile - and written into the main tree only where nothing can be lost, all or nothing.
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
        Assert.Contains("1 path(s) handed to it: 0 its own, 1 inherited, 0 already in the main tree, 0 deleted, 0 settled", applied.Details!);
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

        Assert.Equal(HarnessExit.Refused, (await kit.FoldAsync("ag", apply: true)).ExitCode);

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
    public async Task AFoldRunAgain_FindsItsOwnWritesAlreadyIn()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        Assert.True((await kit.FoldAsync("ag", apply: true)).Succeeded);

        var again = await kit.FoldAsync("ag", apply: true);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Contains("0 inherited path(s) left out; 0 path(s) are its own:", again.Details!);
        Assert.Contains("  b.txt", again.Details!);
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
        Assert.Contains("  b.txt   (not handed to it)", applied.Details!);

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
            Closing = new Core.Orchestration.AgentClosing { At = DateTimeOffset.UtcNow, Abandoned = false, Evidence = string.Empty, Stamp = "1:1", Held = [] },
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

        var stopped = await agents.FoldAsync(kit.Main, "o1", "ag", [], apply: true, TestContext.Current.CancellationToken);

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

        var failed = await agents.FoldAsync(kit.Main, "o1", "ag", [], apply: true, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, failed.ExitCode);
        Assert.Contains("what it changed cannot be read", failed.Message);
        Assert.False(File.Exists(Path.Combine(kit.Main, "new.txt")));
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
