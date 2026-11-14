using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>
/// The tree create-agent gives an agent: a repository of its own, which the main checkout's git records nowhere and whose
/// objects and refs never reach the main checkout, kept standing by keepalives in the main checkout - and the linked
/// worktrees an earlier version gave agents, still read, folded, rebased and deleted.
/// </summary>
public sealed class AgentTreesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// create-agent makes the agent's worktree a repository of its own and records it so: its .git is a directory, the
    /// main checkout's git lists no worktree but the main one, the commit it stands on is kept alive in the main
    /// checkout, and list-worktree lists it by its address as a repository.
    /// </summary>
    [Fact]
    public async Task AnAgentsTree_IsARepositoryOfItsOwn_AndMainsWorktreeListHoldsMainAlone()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);

        var worktree = await kit.CreateAgentAsync("ag");
        var record = kit.Record("ag");

        Assert.Equal(AgentRecord.TreeRepository, record.Tree);
        Assert.True(Directory.Exists(Path.Combine(worktree, ".git")));
        Assert.Equal(record.Base, await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "refs/harness/worktree-base/o1/ag", Token));
        Assert.Equal(record.Base, await kit.Harness.GitClient.ResolveCommitAsync(worktree, "HEAD", Token));
        Assert.Equal([true], (await kit.Harness.GitClient.ListWorktreesAsync(kit.Main, Token)).Select(listed => listed.IsMain));
        Assert.Equal(
            [("o1/ag", WorktreeListing.RepositoryKind)],
            (await kit.Harness.WorktreeService.ListAsync(kit.Main, Token)).Select(listed => (listed.Name, listed.Kind)));
        Assert.Contains("\"tree\": \"repository\"", File.ReadAllText(kit.Layout.AgentRecordFile("ag")), StringComparison.Ordinal);
    }

    /// <summary>
    /// While an agent's base moves, the main checkout keeps both commits alive - the base's keepalive the old one, the
    /// move's the new one - from before anything is written into its worktree; once its record names the new base, the
    /// base's keepalive names it and the move's is gone.
    /// </summary>
    [Fact]
    public async Task ARebase_KeepsBothCommitsAliveUntilItsRecordNamesTheNew()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var to = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;
        var seen = new List<(string? Base, string? Moving, string? Recorded)>();
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            BeforeCheckOut = _ => seen.Add((
                kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "refs/harness/worktree-base/o1/ag", Token).GetAwaiter().GetResult(),
                kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "refs/harness/worktree-moving/o1/ag", Token).GetAwaiter().GetResult(),
                kit.Record("ag").Base)),
        };

        var applied = await kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git).RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.NotEmpty(seen);
        Assert.All(seen, moment => Assert.Equal((from, to, from), moment));
        Assert.Equal(to, kit.Record("ag").Base);
        Assert.Equal(to, await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "refs/harness/worktree-base/o1/ag", Token));
        Assert.Null(await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "refs/harness/worktree-moving/o1/ag", Token));
    }

    /// <summary>
    /// A move whose commit cannot be kept alive in the main checkout is refused before anything is recorded or written.
    /// </summary>
    [Fact]
    public async Task AMoveWhoseKeepaliveCannotBeWritten_IsRefused_NothingChanged()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var from = kit.Record("ag").Base!;
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var to = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            InsteadOfRun = arguments => arguments is ["update-ref", "refs/harness/worktree-moving/o1/ag", ..] ? new Core.Git.GitCommandResult(1, string.Empty, "fatal: cannot lock ref") : null,
        };

        var refused = await kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git).RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token);

        Assert.Equal(HarnessExit.CommandFailed, refused.ExitCode);
        Assert.Equal(
            $"The base of agent 'ag' of 'o1' was not moved: the commit it would move to, {OrchestrationReports.Base(to)}, could not be kept alive in the "
            + "main checkout as 'refs/harness/worktree-moving/o1/ag': fatal: cannot lock ref. Nothing was changed.",
            refused.Message);
        Assert.Equal((from, null), (kit.Record("ag").Base, kit.Record("ag").Moving));
        Assert.Equal("two\n", OrchestrationKit.Read(worktree, "b.txt"));
    }

    /// <summary>
    /// A move whose keepalives cannot be settled once its record names the new base still stands, and says how to settle
    /// them by hand.
    /// </summary>
    [Fact]
    public async Task AKeepaliveThatCannotBeSettled_IsSaid_AndTheMoveStands()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var to = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            InsteadOfRun = arguments => arguments is ["update-ref", "refs/harness/worktree-base/o1/ag", ..] ? new Core.Git.GitCommandResult(1, string.Empty, "fatal: cannot lock ref") : null,
        };

        var applied = await kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git).RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains(
            "its base is recorded in its record, and the main checkout's keepalives of it could not be settled: fatal: cannot lock ref; "
            + $"'git update-ref refs/harness/worktree-base/o1/ag {to}' and 'git update-ref -d refs/harness/worktree-moving/o1/ag' settle them",
            applied.Details!);
        Assert.Equal(to, kit.Record("ag").Base);

        // The move's keepalive still keeps the new base alive, so the agent stays whole.
        Assert.Equal(to, await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "refs/harness/worktree-moving/o1/ag", Token));
        Assert.True((await kit.FoldAsync("ag", apply: false)).Succeeded);
    }

    /// <summary>
    /// An object the agent stores in its own repository under the id of a base blob - content that does not hash to its
    /// name, which git reads before the borrowed one - never reaches the main checkout: the fold reads the base from the
    /// main checkout's store and the agent's files as files, so the main tree gets the agent's bytes exactly and the main
    /// checkout's store holds only its own objects.
    /// </summary>
    [Fact]
    public async Task AnObjectTheAgentPlantsUnderABaseId_NeverReachesMain()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        var baseBlob = (await kit.Harness.RunGitAsync(kit.Main, ["rev-parse", kit.Record("ag").Base + ":b.txt"], Token)).StandardOutput.Trim();

        // A loose object written by hand under the base blob's id, holding other content.
        var planted = Path.Combine(worktree, ".git", "objects", baseBlob[..2], baseBlob[2..]);
        Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
        var content = Encoding.ASCII.GetBytes("planted base\n");

        using (var file = File.Create(planted))
        using (var zlib = new ZLibStream(file, CompressionLevel.Optimal))
        {
            zlib.Write([.. Encoding.ASCII.GetBytes($"blob {content.Length}\0"), .. content]);
        }

        Assert.Equal("planted base\n", (await kit.Harness.RunGitAsync(worktree, ["cat-file", "-p", baseBlob], Token)).StandardOutput);

        OrchestrationKit.Write(worktree, "b.txt", "two\nthe agent's line\n");
        var folded = await kit.FoldAsync("ag", apply: true);

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Equal(Encoding.UTF8.GetBytes("two\nthe agent's line\n"), File.ReadAllBytes(Path.Combine(kit.Main, "b.txt")));
        Assert.Equal("two\n", (await kit.Harness.RunGitAsync(kit.Main, ["cat-file", "-p", baseBlob], Token)).StandardOutput);
        Assert.Equal(string.Empty, (await kit.Harness.RunGitAsync(kit.Main, ["fsck", "--no-dangling", "--no-progress"], Token)).StandardOutput);
    }

    /// <summary>
    /// An agent whose base nothing keeps alive in the main checkout any more is refused by every verb that works on it,
    /// naming the ref that keeps it and the command that writes it again while the commit is there.
    /// </summary>
    [Fact]
    public async Task AMissingKeepalive_RefusesTheFold_NamingTheRemedy()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        var based = kit.Record("ag").Base!;
        await kit.GitAsync(kit.Main, "update-ref", "-d", "refs/harness/worktree-base/o1/ag");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"Agent 'ag' of 'o1': the main checkout keeps nothing alive at 'refs/harness/worktree-base/o1/ag' for {OrchestrationReports.Base(based)}, the "
            + "commit its repository stands on and borrows from the main checkout's store. Nothing was read, written or removed: "
            + $"'git -C {kit.Main} update-ref refs/harness/worktree-base/o1/ag {based}' keeps it again, while it is there.",
            refused.Message);
    }

    /// <summary>
    /// A record that names a repository of its own while a linked worktree stands at its path is refused as no tree of the
    /// agent's, nothing read: the two are not the same tree.
    /// </summary>
    [Fact]
    public async Task ALinkedWorktreeWhereTheRecordNamesARepository_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateLegacyAgentAsync("ag");
        kit.Harness.OrchestrationStore.UpdateAgent(kit.Layout, "ag", current => current with { Tree = AgentRecord.TreeRepository });

        var refused = await kit.FoldAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"Agent 'ag' of 'o1': '{worktree}' is not a worktree of this repository - it is one of git's linked worktrees, and its record says it was made "
            + "a repository of its own. Nothing was read, written or removed.",
            refused.Message);
    }

    /// <summary>
    /// An agent an earlier version made, its worktree one of git's linked worktrees, in a checkout moved by hand: git cannot
    /// open its worktree, whose .git file and git's record name where they were. list-orchestrator says it cannot be worked
    /// on and names git's repair, never that it stands live at its worktree; list-worktree never says to delete it; a fold
    /// is refused naming the repair, nothing touched; and once repaired the agent stands live at its worktree again, its
    /// work in it.
    /// </summary>
    [Fact]
    public async Task ALegacyLinkedAgentOfACheckoutMovedByHand_IsSaidAsOneToRepair_AndNeverAsLiveOrToDelete()
    {
        using var outer = new TempDirectory();
        using var temp = new TempDirectory(outer.Path, "before-");
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var before = await kit.CreateLegacyAgentAsync("ag");
        OrchestrationKit.Write(before, "b.txt", "two\na week of the agent's work\n");

        var main = Path.Combine(outer.Path, "after");
        Directory.Move(temp.Path, main);
        var worktree = Path.Combine(main, Path.GetRelativePath(temp.Path, before));
        var repair = $"Run 'git -C {main} worktree repair {worktree}'";

        var listed = await kit.Harness.OrchestratorService.ListAsync(main, name: null, false, false, json: false, Token);

        Assert.True(listed.Succeeded, OrchestrationKit.Describe(listed));
        var line = Assert.Single(listed.Details ?? [], detail => detail.Contains("ag  live", StringComparison.Ordinal));
        Assert.StartsWith($"ag  live, and its worktree at '{worktree}' cannot be worked on: '{worktree}' is a worktree git records at '{before}'", line.Trim(), StringComparison.Ordinal);
        Assert.Contains(repair, line, StringComparison.Ordinal);

        var json = await kit.Harness.OrchestratorService.ListAsync(main, name: null, false, false, json: true, Token);
        var agent = Assert.Single(JsonNode.Parse(Assert.Single(json.Data))!["orchestrators"]![0]!["agents"]!.AsArray())!;

        Assert.Contains(repair, (string?)agent["worktreeProblem"], StringComparison.Ordinal);
        Assert.DoesNotContain("then delete", kit.Harness.StandardError.ToString(), StringComparison.Ordinal);

        var refused = await kit.Harness.AgentService.FoldAsync(main, OrchestrationKit.Orchestrator, "ag", new FoldAllowances(), apply: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal($"Agent 'ag' of 'o1': {WorktreeInspector.SaidMoved(main, worktree, before)}. Nothing was read, written or removed.", refused.Message);
        Assert.Contains(repair, refused.Message, StringComparison.Ordinal);

        await kit.GitAsync(main, "worktree", "repair", worktree);

        var again = await kit.Harness.OrchestratorService.ListAsync(main, name: null, false, false, json: false, Token);

        Assert.Contains(again.Details ?? [], detail => detail.Trim().StartsWith($"ag  live at '{worktree}'", StringComparison.Ordinal));
        Assert.Equal("two\na week of the agent's work\n", OrchestrationKit.Read(worktree, "b.txt"));
    }

    /// <summary>An agent an earlier version made, its worktree one of git's linked worktrees, is folded as it always was.</summary>
    [Fact]
    public async Task ALegacyLinkedAgent_IsFolded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateLegacyAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nthe agent's line\n");

        var folded = await kit.FoldAsync("ag", apply: true);

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Equal("two\nthe agent's line\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>An agent an earlier version made, its worktree one of git's linked worktrees, has its base moved as it always had.</summary>
    [Fact]
    public async Task ALegacyLinkedAgent_IsRebased()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateLegacyAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var to = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;

        var applied = await kit.Harness.AgentService.RebaseAsync(kit.Main, "o1", "ag", new RebaseRequest(), apply: true, Token);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal(to, kit.Record("ag").Base);
        Assert.Equal(to, await kit.Harness.GitClient.ResolveCommitAsync(worktree, "HEAD", Token));
        Assert.Equal("two\ncommitted\n", OrchestrationKit.Read(worktree, "b.txt"));
        Assert.Null(kit.Record("ag").Tree);
    }

    /// <summary>
    /// An agent an earlier version made, its worktree one of git's linked worktrees, is deleted as it always was: its
    /// worktree, git's record of it and the commit it stood on all go.
    /// </summary>
    [Fact]
    public async Task ALegacyLinkedAgent_IsDeleted()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateLegacyAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nnot kept\n");

        var deleted = await kit.DeleteAsync("ag", apply: true, discard: true);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Equal(AgentStates.Deleted, kit.Record("ag").State);
        Assert.False(Directory.Exists(worktree));
        Assert.Equal([true], (await kit.Harness.GitClient.ListWorktreesAsync(kit.Main, Token)).Select(listed => listed.IsMain));
        Assert.Equal(string.Empty, (await kit.Harness.RunGitAsync(kit.Main, ["for-each-ref", "refs/harness/"], Token)).StandardOutput);
    }

    /// <summary>
    /// An agent's repository deleted with its agent leaves nothing in the main checkout: its directory, both keepalives,
    /// and no copy recorded on a host. One whose directory went before the deletion was asked has its keepalive forgotten
    /// all the same.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletingAnAgent_ForgetsItsKeepalive_WhetherItsRepositoryWasThereOrNot(bool goneBefore)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");

        if (goneBefore)
        {
            kit.Harness.FileSystem.DeleteDirectory(worktree);
        }

        var deleted = await kit.DeleteAsync("ag", apply: true, discard: true);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.False(Directory.Exists(worktree));
        Assert.Equal(string.Empty, (await kit.Harness.RunGitAsync(kit.Main, ["for-each-ref", "refs/harness/"], Token)).StandardOutput);
        Assert.Empty(new Core.Sync.HostCopyRecord(kit.Harness.FileSystem, kit.Harness.Platform.PathComparison).Of(new Core.Repository.HarnessLayout(kit.Main, kit.Main), "o1--ag"));
    }
}
