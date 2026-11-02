using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>
/// An agent's repository: a git repository of its own, borrowing the main checkout's objects through its alternates,
/// holding no ref but its detached HEAD, its configuration the main checkout's included and then its own, and the commit it
/// stands on kept alive in the main checkout before anything of it is made.
/// </summary>
public sealed class AgentRepositoriesTests
{
    private static readonly WorktreeAddress Address = WorktreeAddress.Nested("o1", "a");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A new agent's repository reads the main checkout's objects through its alternates, holds no ref at all, stands
    /// detached at the main checkout's HEAD, and is no worktree git records in the main checkout: nothing done in it can
    /// write the main checkout's refs or objects.
    /// </summary>
    [Fact]
    public async Task ANewAgentRepository_BorrowsMainsObjects_AndHoldsNoRefButItsDetachedHead()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        var (main, path) = await PlaceAsync(harness, temp);
        var head = await harness.GitClient.ResolveCommitAsync(main, "HEAD", Token);

        var made = await Repositories(harness).MakeAsync(main, Address, path, Token);

        Assert.Null(made.Failure);
        Assert.Equal(head, made.BaseCommit);
        Assert.Equal(head, await harness.GitClient.ResolveCommitAsync(path, "HEAD", Token));
        Assert.Equal(head, await harness.GitClient.ResolveCommitAsync(main, "refs/harness/worktree-base/o1/a", Token));
        Assert.True(Directory.Exists(Path.Combine(path, ".git")));
        Assert.Equal("test repository", File.ReadAllText(Path.Combine(path, "README.md")));

        // Detached: HEAD names no branch, and no ref of any kind is there to name one.
        Assert.Equal(1, (await harness.GitClient.RunAsync(path, ["symbolic-ref", "--quiet", "HEAD"], cancellationToken: Token)).ExitCode);
        Assert.Equal(string.Empty, (await harness.RunGitAsync(path, ["for-each-ref"], Token)).StandardOutput);
        Assert.Equal(
            [$"alternate: {await CommonAsync(harness, main)}/objects"],
            (await harness.RunGitAsync(path, ["count-objects", "-v"], Token)).OutputLines.Where(line => line.StartsWith("alternate:", StringComparison.Ordinal)));
        Assert.Equal(string.Empty, (await harness.RunGitAsync(path, ["status", "--porcelain"], Token)).StandardOutput);

        // The main checkout records no worktree: the repository is not one of its linked worktrees.
        var worktrees = await harness.GitClient.ListWorktreesAsync(main, Token);
        Assert.Equal([(main, true)], worktrees.Select(worktree => (worktree.Path, worktree.IsMain)));
    }

    /// <summary>
    /// Its configuration includes the main checkout's first, so filters, line endings, remotes and identity are the main
    /// checkout's, live; then what git init probed of the file system; then its own keys, which outrank both: no automatic
    /// housekeeping, the main checkout's large-file store, and the mark naming the main checkout and the agent.
    /// </summary>
    [Fact]
    public async Task ItsConfiguration_IncludesMains_ThenItsOwnKeys_InThatOrder()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        var (main, path) = await PlaceAsync(harness, temp);
        var common = await CommonAsync(harness, main);

        var made = await Repositories(harness).MakeAsync(main, Address, path, Token);

        Assert.Null(made.Failure);

        // What git init writes of this file system, read from a repository made beside it the same way.
        var probe = temp.Combine("probe");
        await harness.RunGitAsync(temp.Path, ["-c", "init.defaultRefFormat=files", "init", "--quiet", "--template=", "--object-format=sha1", probe], Token);

        Assert.Equal(
            [
                $"include.path={common}/config",
                .. (await harness.RunGitAsync(probe, ["config", "--file", Path.Combine(probe, ".git", "config"), "--list"], Token)).OutputLines,
                "gc.auto=0",
                "maintenance.auto=false",
                "fetch.writecommitgraph=false",
                $"lfs.storage={common}/lfs",
                $"harness.maincheckout={main}",
                "harness.agent=o1/a",
            ],
            (await harness.RunGitAsync(path, ["config", "--file", Path.Combine(path, ".git", "config"), "--list"], Token)).OutputLines);

        // Live: the identity the main checkout's configuration names is the repository's, read through the include.
        Assert.Equal("harness@test.invalid", (await harness.RunGitAsync(path, ["config", "user.email"], Token)).StandardOutput.Trim());
    }

    /// <summary>
    /// The commit is kept alive in the main checkout before anything of the repository is made, and where that cannot be
    /// done nothing is made at all: a repository whose commit nothing keeps alive loses it to the main checkout's first
    /// prune once its branch moves on.
    /// </summary>
    [Fact]
    public async Task ItsKeepalive_IsWrittenBeforeAnythingIsMade_AndAFailureMakesNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        var (main, path) = await PlaceAsync(harness, temp);
        var head = await harness.GitClient.ResolveCommitAsync(main, "HEAD", Token);

        var recorded = new InterceptingGitClient(harness.GitClient);
        Assert.Null((await Repositories(harness, recorded).MakeAsync(main, Address, path, Token)).Failure);

        // The order of what was run, by subcommand: the keepalive before git init.
        var order = recorded.Runs.Select(run => run.Arguments.SkipWhile(argument => argument == "-c" || argument.Contains('=', StringComparison.Ordinal)).First()).ToList();
        Assert.Equal(["rev-parse", "config", "update-ref", "init", "checkout", "count-objects"], order);

        var refusing = new InterceptingGitClient(harness.GitClient)
        {
            InsteadOfRun = arguments => arguments is ["update-ref", ..] ? new GitCommandResult(128, string.Empty, "fatal: the ref store is read-only") : null,
        };
        var second = Path.Combine(Path.GetDirectoryName(path)!, "b");

        var refused = await Repositories(harness, refusing).MakeAsync(main, WorktreeAddress.Nested("o1", "b"), second, Token);

        Assert.Null(refused.BaseCommit);
        Assert.Equal(HarnessExit.CommandFailed, refused.Failure!.ExitCode);
        Assert.Equal(
            $"The repository 'o1/b' works in could not be made at '{second}': the commit it is made at, {head![..12]}, could not be kept alive in the main "
            + "checkout as 'refs/harness/worktree-base/o1/b': fatal: the ref store is read-only. Nothing was created.",
            refused.Failure.Message);
        Assert.False(Directory.Exists(second));
        Assert.DoesNotContain(refusing.Runs, run => run.Arguments.Contains("init", StringComparer.Ordinal));
    }

    /// <summary>
    /// A repository that fails part way - here its checkout - is taken back whole: its directory and the keepalive written
    /// for it go, and what is said is that nothing was created.
    /// </summary>
    [Fact]
    public async Task AFailedCheckout_TakesBackTheDirectoryAndTheKeepalive()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        var (main, path) = await PlaceAsync(harness, temp);
        var head = await harness.GitClient.ResolveCommitAsync(main, "HEAD", Token);

        // git's own checkout runs and writes the tree; its answer is then a failure, so the tree is there to be taken back.
        var failing = new InterceptingGitClient(harness.GitClient)
        {
            AfterRun = (arguments, result) => arguments is ["checkout", ..] ? new GitCommandResult(1, string.Empty, "error: the checkout was stopped") : result,
        };

        var made = await Repositories(harness, failing).MakeAsync(main, Address, path, Token);

        Assert.Equal(HarnessExit.CommandFailed, made.Failure!.ExitCode);
        Assert.Equal(
            $"The repository 'o1/a' works in could not be made at '{path}': git could not check out {head![..12]} in it: error: the checkout was stopped. "
            + "What was made of it was taken back: nothing was created.",
            made.Failure.Message);
        Assert.False(Directory.Exists(path));
        Assert.Null(await harness.GitClient.ResolveCommitAsync(main, "refs/harness/worktree-base/o1/a", Token));
    }

    /// <summary>
    /// Made from a main checkout whose history is cut short, the repository is cut short in the same places, so a walk of
    /// its history stops there rather than looking for parents neither store holds.
    /// </summary>
    [Fact]
    public async Task AShallowMain_GivesItsShallowFile()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var origin = temp.Combine("origin");
        Directory.CreateDirectory(origin);
        await harness.InitializeGitRepositoryAsync(origin, Token);
        File.WriteAllText(Path.Combine(origin, "second.txt"), "second\n");
        await harness.CommitAllAsync(origin, "second", Token);

        var shallow = temp.Combine("main");
        await harness.RunGitAsync(temp.Path, ["clone", "--quiet", "--depth", "1", new Uri(origin).AbsoluteUri, shallow], Token);
        var main = (await harness.GitClient.GetMainWorktreeAsync(shallow, Token))!.Path;
        var path = Path.Combine(main, "trees", "o1", "a");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var made = await Repositories(harness).MakeAsync(main, Address, path, Token);

        Assert.Null(made.Failure);
        Assert.Equal(File.ReadAllText(Path.Combine(main, ".git", "shallow")), File.ReadAllText(Path.Combine(path, ".git", "shallow")));
        Assert.Equal("true", (await harness.RunGitAsync(path, ["rev-parse", "--is-shallow-repository"], Token)).StandardOutput.Trim());
        Assert.Equal("1", (await harness.RunGitAsync(path, ["rev-list", "--count", "HEAD"], Token)).StandardOutput.Trim());
    }

    /// <summary>A main checkout whose objects are named by SHA-256 gives a repository that names them the same way, or it could read none of them.</summary>
    [Fact]
    public async Task ASha256Main_GivesASha256Repository()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.RunGitAsync(temp.Path, ["init", "--quiet", "--object-format=sha256", "."], Token);
        await harness.RunGitAsync(temp.Path, ["config", "user.email", "harness@test.invalid"], Token);
        await harness.RunGitAsync(temp.Path, ["config", "user.name", "Harness Test"], Token);
        temp.WriteFile("a.txt", "a\n");
        await harness.CommitAllAsync(temp.Path, "one", Token);
        var (main, path) = await PlaceAsync(harness, temp);
        var head = await harness.GitClient.ResolveCommitAsync(main, "HEAD", Token);

        var made = await Repositories(harness).MakeAsync(main, Address, path, Token);

        Assert.Null(made.Failure);
        Assert.Equal(64, head!.Length);
        Assert.Equal(head, await harness.GitClient.ResolveCommitAsync(path, "HEAD", Token));
        Assert.Equal("sha256", (await harness.RunGitAsync(path, ["rev-parse", "--show-object-format"], Token)).StandardOutput.Trim());
        Assert.Equal("a\n", File.ReadAllText(Path.Combine(path, "a.txt")));
    }

    /// <summary>
    /// A file git-lfs keeps is checked out from the main checkout's large-file store, which the repository names as its
    /// own: it keeps no store of its own, and nothing of the main checkout's is linked or copied into it.
    /// </summary>
    [Fact]
    public async Task ItsLfsStorage_IsMains_AndNothingIsLinkedIntoIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        Assert.SkipUnless((await harness.GitClient.RunAsync(temp.Path, ["lfs", "version"], cancellationToken: Token)).Succeeded, "git-lfs is not installed, and the store an agent's repository shares is git-lfs's.");

        // Its filters in the main checkout's own configuration alone, which the repository includes; no hooks.
        await harness.RunGitAsync(temp.Path, ["lfs", "install", "--local", "--skip-repo"], Token);
        temp.WriteFile(".gitattributes", "*.bin filter=lfs diff=lfs merge=lfs -text\n");
        temp.WriteFile("big.bin", "large contents\n");
        await harness.CommitAllAsync(temp.Path, "large", Token);
        var (main, path) = await PlaceAsync(harness, temp);
        var common = await CommonAsync(harness, main);

        var made = await Repositories(harness).MakeAsync(main, Address, path, Token);

        Assert.Null(made.Failure);
        Assert.Equal("large contents\n", File.ReadAllText(Path.Combine(path, "big.bin")));
        Assert.Equal($"{common}/lfs", (await harness.RunGitAsync(path, ["config", "lfs.storage"], Token)).StandardOutput.Trim());
        Assert.False(Directory.Exists(Path.Combine(path, ".git", "lfs")));
    }

    /// <summary>
    /// Once the main checkout's branch moves on and its gc prunes everything unreachable, the commit the repository stands
    /// on is still in the store it borrows from - its keepalive reaches it - and git still works there.
    /// </summary>
    [Fact]
    public async Task MainsGcAfterItsBranchMovesOn_KeepsTheBase()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        temp.WriteFile("a.txt", "only here\n");
        await harness.CommitAllAsync(temp.Path, "base", Token);
        var (main, path) = await PlaceAsync(harness, temp);
        var head = await harness.GitClient.ResolveCommitAsync(main, "HEAD", Token);

        Assert.Null((await Repositories(harness).MakeAsync(main, Address, path, Token)).Failure);

        // The branch leaves the base behind, nothing but the keepalive reaches it, and gc prunes all else at once.
        File.WriteAllText(Path.Combine(main, "a.txt"), "moved on\n");
        await harness.RunGitAsync(main, ["commit", "--quiet", "--all", "--amend", "-m", "moved on"], Token);
        await harness.RunGitAsync(main, ["reflog", "expire", "--expire=now", "--all"], Token);
        await harness.RunGitAsync(main, ["gc", "--quiet", "--prune=now"], Token);

        Assert.True((await harness.GitClient.RunAsync(main, ["cat-file", "-e", head + "^{commit}"], cancellationToken: Token)).Succeeded);
        Assert.Equal(string.Empty, (await harness.RunGitAsync(path, ["status", "--porcelain"], Token)).StandardOutput);
        Assert.Equal("only here\n", (await harness.RunGitAsync(path, ["show", "HEAD:a.txt"], Token)).StandardOutput);
    }

    /// <summary>
    /// A command run inside an agent's repository, which git names its own main checkout, finds the main checkout its mark
    /// names: the repository is the tree acted on, and every piece of the harness's state - the worktrees root, the run
    /// lock, the orchestrators - resolves in the main checkout, as from inside a linked worktree.
    /// </summary>
    [Fact]
    public async Task LocatingFromInsideAnAgentRepository_FindsItsMainCheckout()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        var (main, path) = await PlaceAsync(harness, temp);
        Assert.Null((await Repositories(harness).MakeAsync(main, Address, path, Token)).Failure);
        Directory.CreateDirectory(Path.Combine(path, "deeper"));

        foreach (var start in new[] { path, Path.Combine(path, "deeper") })
        {
            var layout = await harness.RepositoryLocator.LocateAsync(start, Token);

            PathAssert.Same(path, layout!.RepositoryRoot);
            Assert.Equal(main, layout.MainCheckoutRoot);
            Assert.True(layout.IsWorktree(harness.Platform));
        }

        // The main checkout is still its own.
        var mains = await harness.RepositoryLocator.LocateAsync(main, Token);
        Assert.Equal(new Core.Repository.HarnessLayout(main, main), mains);
    }

    /// <summary>
    /// A repository whose mark names a main checkout that does not lend it its objects is refused, with why: never taken for
    /// a main checkout of its own, which would put every piece of the harness's state inside the agent's tree.
    /// </summary>
    [Fact]
    public async Task AMarkedRepositoryWhoseMainDoesNotLendIt_IsRefused()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        var (main, path) = await PlaceAsync(harness, temp);
        Assert.Null((await Repositories(harness).MakeAsync(main, Address, path, Token)).Failure);
        var common = await CommonAsync(harness, main);
        var root = (await harness.GitClient.GetRepositoryRootAsync(path, Token))!;

        File.WriteAllText(Path.Combine(path, ".git", "objects", "info", "alternates"), string.Empty);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => harness.RepositoryLocator.LocateAsync(path, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{root}' is an agent's repository of '{main}', which does not lend it its objects: '{common}/objects' is not among the object "
            + "directories git reads there. DssHarness keeps its state in the main checkout, and so runs nothing from here; run it from a "
            + $"main checkout, and delete '{root}' once nothing in it is needed.",
            refused.Message);
    }

    /// <summary>
    /// list-worktree lists an agent's repository by its address with the commit it was made at, beside a plain linked
    /// worktree, each said in --json as the kind of tree it is: a repository of its own, or one of git's worktrees.
    /// </summary>
    [Fact]
    public async Task ListWorktree_ListsAnAgentRepositoryByItsAddress_WithItsBase()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(temp.Path, Token, new Core.Configuration.HarnessConfig { Worktrees = OrchestrationKit.Settings });
        var main = (await harness.GitClient.GetMainWorktreeAsync(temp.Path, Token))!.Path;
        var root = new Core.Repository.HarnessLayout(main, main).WorktreesDirectoryUnder(OrchestrationKit.Settings.Root);
        var path = Path.Combine(root, "o1", "a");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var head = (await harness.GitClient.ResolveCommitAsync(main, "HEAD", Token))!;

        Assert.Null((await Repositories(harness).MakeAsync(main, Address, path, Token)).Failure);
        var plain = await harness.WorktreeService.CreateAsync(main, "wt", useRandomName: false, Token);
        Assert.True(plain.Succeeded, plain.Outcome.Message);

        var listed = await harness.WorktreeService.ListAsync(main, Token);
        var text = WorktreeReports.List(listed, new HostCopyListing(), json: false);
        var json = WorktreeReports.List(listed, new HostCopyListing(), json: true);

        Assert.Equal("2 worktree(s)", text.Message);
        Assert.Equal([$"o1/a  base {head[..12]}", $"wt  base {head[..12]}"], text.Details);
        Assert.True(
            System.Text.Json.Nodes.JsonNode.DeepEquals(
                new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject { ["name"] = "o1/a", ["kind"] = "repository", ["baseCommit"] = head, ["path"] = harness.FileSystem.ResolveLinks(path), ["copies"] = new System.Text.Json.Nodes.JsonArray() },
                    new System.Text.Json.Nodes.JsonObject { ["name"] = "wt", ["kind"] = "worktree", ["baseCommit"] = head, ["path"] = harness.FileSystem.ResolveLinks(plain.Path), ["copies"] = new System.Text.Json.Nodes.JsonArray() }),
                System.Text.Json.Nodes.JsonNode.Parse((string)json.Data![0])!["worktrees"]),
            (string)json.Data![0]);

        // Asked from inside the agent's repository, the command answers as from the main checkout.
        var fromMain = await CliRunner.RunAsync(["list-worktree", "--json", "-C", main], Token);
        var fromInside = await CliRunner.RunAsync(["list-worktree", "--json", "-C", path], Token);

        Assert.Equal(0, fromMain.ExitCode);
        Assert.Equal(fromMain.StandardOutput, fromInside.StandardOutput);
        Assert.Equal(fromMain.ExitCode, fromInside.ExitCode);
    }

    /// <summary>
    /// An orchestrator is not deleted while an agent's repository is below its directory, which is said as the worktree it
    /// is, by its address - never as a directory git records nothing at, which the person would be told to look in and
    /// delete by hand.
    /// </summary>
    [Fact]
    public async Task DeleteOrchestrator_RefusesAnAgentRepositoryBelow_AsAWorktreeNotAsUnrecorded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var main = (await kit.Harness.GitClient.GetMainWorktreeAsync(kit.Main, Token))!.Path;
        Directory.CreateDirectory(kit.Worktrees);
        Assert.Null((await Repositories(kit.Harness).MakeAsync(main, Address, kit.Worktree("a"), Token)).Failure);

        var refused = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, OrchestrationKit.Orchestrator, deleteEvidence: false, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"Orchestrator 'o1' was not deleted: worktrees are left below '{kit.Worktrees}' - o1/a. Delete each with "
            + $"'{ToolPackage.Command} delete-worktree <address>' first. Nothing was changed.",
            refused.Message);
        Assert.True(Directory.Exists(Path.Combine(kit.Worktree("a"), ".git")));
    }

    private static AgentRepositories Repositories(HarnessFactory harness, IGitClient? gitClient = null)
        => new(gitClient ?? harness.GitClient, harness.FileSystem, harness.Platform);

    /// <summary>The main checkout as git spells it, and where its agent's repository goes below it, with its parent made.</summary>
    private static async Task<(string Main, string Path)> PlaceAsync(HarnessFactory harness, TempDirectory temp)
    {
        var main = (await harness.GitClient.GetMainWorktreeAsync(temp.Path, Token))!.Path;
        var path = Path.Combine(main, "trees", "o1", "a");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return (main, path);
    }

    /// <summary>The main checkout's git directory, as git spells it absolute.</summary>
    private static async Task<string> CommonAsync(HarnessFactory harness, string main)
        => (await harness.RunGitAsync(main, ["rev-parse", "--path-format=absolute", "--git-common-dir"], Token)).StandardOutput.Trim();
}
