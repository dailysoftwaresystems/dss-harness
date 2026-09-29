using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>An orchestrator's agents' worktrees, made below the directory named for it under the worktrees root.</summary>
public sealed class NestedWorktreeTests
{
    private static readonly WorktreeSettings Relaxed = new() { PathBudgetReserve = 5, PathBudgetMargin = 2 };

    /// <summary>
    /// An agent's worktree is made below the directory named for its orchestrator, the commit it was made from recorded
    /// under its address and handed back, and listed by both names beside the plain worktrees.
    /// </summary>
    [Fact]
    public async Task AnAgentsWorktree_IsMadeBelowItsOrchestrator_AndListedByBothNames()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var created = await harness.WorktreeService.CreateAtAsync(temp.Path, WorktreeAddress.Nested("o1", "api"), cancellationToken);
        var plain = await harness.WorktreeService.CreateAsync(temp.Path, "wt", useRandomName: false, cancellationToken);

        Assert.True(created.Succeeded, created.Outcome.Message);
        Assert.True(plain.Succeeded, plain.Outcome.Message);
        Assert.Equal("o1/api", created.Name);
        PathAssert.Same(Path.Combine(Root(temp), "o1", "api"), created.Path);
        Assert.Equal(
            (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!,
            created.BaseCommit);
        Assert.Equal(created.BaseCommit, await harness.GitClient.ResolveCommitAsync(temp.Path, "refs/harness/worktree-base/o1/api", cancellationToken));

        var listed = await harness.WorktreeService.ListAsync(temp.Path, cancellationToken);

        Assert.Equal(["o1/api", "wt"], listed.Select(worktree => worktree.Name));
        Assert.Equal(created.BaseCommit, listed[0].BaseCommit);
    }

    /// <summary>
    /// Deleted by its address, an agent's worktree goes with git's record of it and the commit it was made from; the
    /// orchestrator's other agents are untouched.
    /// </summary>
    [Fact]
    public async Task AnAgentsWorktree_IsDeletedByItsAddress_AndItsSiblingsStay()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.WorktreeService.CreateAtAsync(temp.Path, WorktreeAddress.Nested("o1", "api"), cancellationToken);
        await harness.WorktreeService.CreateAtAsync(temp.Path, WorktreeAddress.Nested("o1", "web"), cancellationToken);

        var deleted = await harness.WorktreeService.DeleteAsync(temp.Path, "o1/api", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.Equal("removed worktree 'o1/api'", deleted.Outcome.Message);
        Assert.False(Directory.Exists(Path.Combine(Root(temp), "o1", "api")));
        Assert.True(Directory.Exists(Path.Combine(Root(temp), "o1", "web")));
        Assert.Null(await harness.GitClient.ResolveCommitAsync(temp.Path, "refs/harness/worktree-base/o1/api", cancellationToken));
        Assert.Equal(["o1/web"], (await harness.WorktreeService.ListAsync(temp.Path, cancellationToken)).Select(worktree => worktree.Name));
    }

    /// <summary>
    /// A directory holding worktrees below it - an orchestrator's, holding its agents' - is never deleted as one, and
    /// --force does not change that: deleting it would delete each of them.
    /// </summary>
    [Fact]
    public async Task ADirectoryHoldingAgentsWorktrees_IsRefused_EvenForced()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.WorktreeService.CreateAtAsync(temp.Path, WorktreeAddress.Nested("o1", "api"), cancellationToken);
        await harness.WorktreeService.CreateAtAsync(temp.Path, WorktreeAddress.Nested("o1", "web"), cancellationToken);

        foreach (var force in new[] { false, true })
        {
            var refused = await harness.WorktreeService.DeleteAsync(temp.Path, "o1", force, deleteEvidence: false, cancellationToken: cancellationToken);

            Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
            Assert.Equal(
                $"'{Path.Combine(Root(temp), "o1")}' is not deleted, --force or not: it holds worktrees below it - o1/api, o1/web - and deleting it "
                + $"would delete each of them. Delete each on its own, with '{ToolPackage.Command} delete-worktree <address>' or, for an "
                + "orchestrator's agent, delete-agent.",
                refused.Outcome.Message);
        }

        Assert.Equal(["o1/api", "o1/web"], (await harness.WorktreeService.ListAsync(temp.Path, cancellationToken)).Select(worktree => worktree.Name));
    }

    /// <summary>create-worktree makes a plain worktree only: an agent's is made with its records, by create-agent.</summary>
    [Fact]
    public async Task CreateWorktree_RefusesAnAgentsAddress_NamingCreateAgent()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        var refused = await harness.WorktreeService.CreateAsync(temp.Path, "o1/api", useRandomName: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, refused.Outcome.ExitCode);
        Assert.Equal(
            "'o1/api' names an orchestrator's agent, whose worktree create-agent makes with its records; create-worktree makes a plain one.",
            refused.Outcome.Message);
    }

    /// <summary>Plain worktrees and orchestrators share the names under the root: a plain one cannot take an orchestrator's.</summary>
    [Fact]
    public async Task APlainWorktree_CannotTakeAnOrchestratorsName()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        Directory.CreateDirectory(temp.Combine(HarnessLayout.OrchestratorsDirectoryName, "o1"));

        var refused = await harness.WorktreeService.CreateAsync(temp.Path, "o1", useRandomName: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.Equal(
            $"'o1' is an orchestrator's name, and its agents' worktrees are made under '{Path.Combine(Root(temp), "o1")}'; choose another name.",
            refused.Outcome.Message);
    }

    /// <summary>An agent's worktree is never made inside a plain worktree, where it would be taken for part of it.</summary>
    [Fact]
    public async Task AnAgentsWorktree_IsNeverMadeInsideAPlainOne()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.WorktreeService.CreateAsync(temp.Path, "o1", useRandomName: false, cancellationToken);

        var refused = await harness.WorktreeService.CreateAtAsync(temp.Path, WorktreeAddress.Nested("o1", "api"), cancellationToken);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.Equal(
            $"'{Path.Combine(Root(temp), "o1")}' is a worktree, and an orchestrator's agents' worktrees cannot be made inside one; delete it, "
            + "or name the orchestrator otherwise.",
            refused.Outcome.Message);
    }

    /// <summary>Each name in an agent's address is held to worktrees.maxNameLength, as a plain worktree's is.</summary>
    [Fact]
    public async Task EachNameOfAnAgentsAddress_IsHeldToTheConfiguredLength()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp, new WorktreeSettings { MaxNameLength = 4, PathBudgetReserve = 5, PathBudgetMargin = 2 });

        var refused = await harness.WorktreeService.CreateAtAsync(temp.Path, WorktreeAddress.Nested("o1", "toolong"), TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, refused.Outcome.ExitCode);
        Assert.StartsWith("'toolong' is 7 characters; the limit is 4.", refused.Outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The copies a gone agent left on hosts are listed by the address a person types, with the delete-worktree that deals
    /// with them, never by the two-hyphen name their directories bear.
    /// </summary>
    [Fact]
    public void AGoneAgentsCopies_AreListedByItsAddress()
    {
        var copy = new HostCopyEntry("o1--api", "ssh pi", "/home/dev/repo.worktree-o1--api", "/repo/.worktrees/o1/api");
        var listing = new HostCopyListing { Gone = [new RecordedTree("o1--api", "/repo/.worktrees/o1/api", [copy])] };

        var lines = WorktreeReports.List([], listing, json: false).Details!;
        var json = WorktreeReports.List([], listing, json: true).Data!.Single();

        Assert.Contains(
            $"o1/api, gone from '/repo/.worktrees/o1/api': '{ToolPackage.Command} delete-worktree o1/api' deals with the copies it left",
            lines);
        Assert.Contains("\"name\": \"o1/api\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"deletedBy\": \"{ToolPackage.Command} delete-worktree o1/api\"", json, StringComparison.Ordinal);
    }

    private static string Root(TempDirectory temp) => Path.Combine(temp.Path, WorktreeSettings.DefaultRoot.Replace('/', Path.DirectorySeparatorChar));

    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp, WorktreeSettings? settings = null)
    {
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken, new HarnessConfig { Worktrees = settings ?? Relaxed });

        return harness;
    }
}
