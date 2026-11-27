using DssHarness.Core.Execution;
using DssHarness.Core.Git;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>
/// What an engine's session holds of where its trees are (<see cref="DssHarness.Core.Repository.SessionTrees"/>): each tree
/// located from its root once in the session and at every load outside one, a directory inside a tree never kept, a
/// tree made again found anew, and an agent's tree made, told and deleted starting only the git its work needs - each
/// git untranslated.
/// </summary>
public sealed class SessionTreesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A repository with one orchestrator, every program its services start counted.</summary>
    private static async Task<(OrchestrationKit Kit, CountingProcesses Counted)> PrepareAsync(TempDirectory temp)
    {
        CountingProcesses? counted = null;
        var kit = await OrchestrationKit.PrepareAsync(temp, processes: inner => counted = new CountingProcesses(inner));

        return (kit, counted!);
    }

    /// <summary>The subcommand of each git <paramref name="work"/> starts, in order.</summary>
    private static async Task<IReadOnlyList<string>> GitsOfAsync(CountingProcesses counted, Func<Task> work)
    {
        var from = counted.Started.Count;
        await work();

        return [.. counted.Started.Skip(from).Select(CountingProcesses.Named)];
    }

    /// <summary>What locating a main checkout from its root asks of git.</summary>
    private static readonly string[] Located = ["git rev-parse", "git worktree", "git rev-parse"];

    /// <summary>What locating an agent's tree from its root asks of git, with its main checkout located and its objects known.</summary>
    private static readonly string[] LocatedAgent = ["git rev-parse", "git worktree", "git rev-parse", "git config", "git count-objects"];

    /// <summary>
    /// What the making of an agent's tree asks of git: the main checkout's HEAD; the files of its commit - where
    /// <paramref name="listed"/>, for the room they take is measured where this machine holds a path to a limit, once for
    /// each commit; where the main checkout keeps its objects, with their format; the settings it would carry wrong; the
    /// keepalive; the repository made and checked out; and its HEAD and its borrowing checked.
    /// </summary>
    internal static IReadOnlyList<string> Made(bool listed)
        => ["git rev-parse", .. listed ? ["git ls-tree"] : Array.Empty<string>(), "git rev-parse", "git config", "git update-ref", "git init", "git checkout", "git rev-parse", "git count-objects"];

    /// <summary>
    /// Inside an engine's session, a tree loaded from its root is located once - every later load of it answered from the
    /// session, starting no git - and outside one at every load; a directory inside a tree is located at every load in a
    /// session too, for it may become a tree of its own.
    /// </summary>
    [Fact]
    public async Task ATreeIsLocatedOnceInASessionFromItsRoot_AndAtEveryLoadOutsideOne_OrFromADirectoryInsideIt()
    {
        using var temp = new TempDirectory();
        var (kit, counted) = await PrepareAsync(temp);
        var docs = Path.Combine(kit.Main, "docs");

        Assert.Equal(Located, await GitsOfAsync(counted, () => kit.Harness.ContextLoader.LoadAsync(kit.Main, Token)));
        Assert.Equal(Located, await GitsOfAsync(counted, () => kit.Harness.ContextLoader.LoadAsync(kit.Main, Token)));

        using (EngineSession.Begin())
        {
            Assert.Equal(Located, await GitsOfAsync(counted, () => kit.Harness.ContextLoader.LoadAsync(kit.Main, Token)));
            Assert.Empty(await GitsOfAsync(counted, () => kit.Harness.ContextLoader.LoadAsync(kit.Main, Token)));
            Assert.Equal(Located, await GitsOfAsync(counted, () => kit.Harness.ContextLoader.LoadAsync(docs, Token)));
            Assert.Equal(Located, await GitsOfAsync(counted, () => kit.Harness.ContextLoader.LoadAsync(docs, Token)));
            Assert.Equal(kit.Main, (await kit.Harness.ContextLoader.LoadAsync(kit.Main, Token)).Layout.MainCheckoutRoot, StringComparer.OrdinalIgnoreCase);
        }

        // The session's end takes what it held with it.
        Assert.Equal(Located, await GitsOfAsync(counted, () => kit.Harness.ContextLoader.LoadAsync(kit.Main, Token)));
    }

    /// <summary>
    /// A worktree the session located is answered from the session until it is deleted: made again under the same name, at
    /// the same path, it is located anew, as the tree it now is.
    /// </summary>
    [Fact]
    public async Task ATreeDeletedAndMadeAgainInASession_IsLocatedAnew()
    {
        using var temp = new TempDirectory();
        var (kit, counted) = await PrepareAsync(temp);

        using var session = EngineSession.Begin();
        _ = await kit.Harness.ContextLoader.LoadAsync(kit.Main, Token);
        var made = await kit.Harness.WorktreeService.CreateAsync(kit.Main, "wt", useRandomName: false, Token);
        Assert.True(made.Succeeded, made.Outcome.Message);
        var tree = made.Path;

        Assert.Equal(Located, await GitsOfAsync(counted, () => kit.Harness.ContextLoader.LoadAsync(tree, Token)));
        Assert.Empty(await GitsOfAsync(counted, () => kit.Harness.ContextLoader.LoadAsync(tree, Token)));

        var deleted = await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "wt", force: false, deleteEvidence: false, cancellationToken: Token);
        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        var again = await kit.Harness.WorktreeService.CreateAsync(kit.Main, "wt", useRandomName: false, Token);
        Assert.True(again.Succeeded, again.Outcome.Message);
        PathAssert.Same(tree, again.Path);

        Assert.Equal(Located, await GitsOfAsync(counted, () => kit.Harness.ContextLoader.LoadAsync(tree, Token)));

        var layout = (await kit.Harness.ContextLoader.LoadAsync(tree, Token)).Layout;

        PathAssert.Same(tree, layout.RepositoryRoot);
        PathAssert.Same(kit.Main, layout.MainCheckoutRoot);
    }

    /// <summary>
    /// In an engine's session that located its main checkout, an agent's tree made, deleted, made and told, and deleted
    /// again starts only the git its work needs: the main checkout's HEAD read once for the making and the room its files
    /// take, those files listed once for every tree made at the commit, where the main checkout keeps its objects kept
    /// from the making's own asking, the main checkout's git directory asked once, and an agent's repository's mark and
    /// borrowing asked once of each tree, whichever of its telling and its locating asks first. Every git runs untranslated.
    /// </summary>
    [Fact]
    public async Task InASession_AnAgentsTreeMadeToldAndDeleted_StartsOnlyTheGitItsWorkNeeds_EachUntranslated()
    {
        using var temp = new TempDirectory();
        var (kit, counted) = await PrepareAsync(temp);
        var bounded = kit.Harness.PathBudget.Bounds();
        var from = counted.Started.Count;

        using (EngineSession.Begin())
        {
            Assert.Equal(Located, await GitsOfAsync(counted, () => kit.Harness.ContextLoader.LoadAsync(kit.Main, Token)));
            Assert.Equal(Made(listed: bounded), await GitsOfAsync(counted, () => kit.CreateReadingAgentAsync("ag")));

            // Deleted unlocated: the main checkout's git directory and the tree's, the tree told as the agent's by its mark
            // and its borrowing, its findings, its own configuration's tree located - its mark taken from its telling - the
            // look again before its files go, and its keepalives.
            Assert.Equal(
                [
                    "git rev-parse", "git rev-parse", "git config", "git count-objects",
                    "git status", "git ls-files", "git rev-parse", "git rev-list",
                    "git rev-parse", "git worktree", "git rev-parse",
                    "git status", "git rev-parse", "git rev-list",
                    "git update-ref", "git update-ref",
                ],
                await GitsOfAsync(counted, () => DeletedAsync(kit, "ag")));

            // Made again at the same commit: its files are not listed again.
            Assert.Equal(Made(listed: false), await GitsOfAsync(counted, () => kit.CreateReadingAgentAsync("ag2")));

            // Located, its main checkout's objects known from the making.
            Assert.Equal(LocatedAgent, await GitsOfAsync(counted, () => kit.Harness.ContextLoader.LoadAsync(kit.Worktree("ag2"), Token)));

            // Deleted once located: neither its mark nor its borrowing is asked again, nor the main checkout's git directory.
            Assert.Equal(
                [
                    "git rev-parse",
                    "git status", "git ls-files", "git rev-parse", "git rev-list",
                    "git status", "git rev-parse", "git rev-list",
                    "git update-ref", "git update-ref",
                ],
                await GitsOfAsync(counted, () => DeletedAsync(kit, "ag2")));
        }

        var gits = counted.Started.Skip(from).Where(started => started.FileName == "git").ToList();

        // Untranslated, but the checkout an agent's tree is made with, which git runs hooks for: it keeps the person's locale.
        Assert.Contains(gits, git => CountingProcesses.Named(git) == "git checkout");
        Assert.All(gits, git => Assert.Equal(CountingProcesses.Named(git) == "git checkout" ? null : GitOwnSettings.Locale, git.Environment.GetValueOrDefault(GitOwnSettings.LocaleVariable)));
    }

    private static async Task DeletedAsync(OrchestrationKit kit, string agent)
        => Assert.True((await kit.DeleteAsync(agent, apply: true, discard: true)).Succeeded);
}
