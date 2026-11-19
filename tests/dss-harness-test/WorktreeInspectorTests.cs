using DssHarness.Core.Configuration;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>
/// What the look a deletion takes at a worktree reads of git's own files: a <c>.git</c> file's first line whole, however
/// long, and the directories that may hold its submodules' repositories, each walked once however their links lead.
/// </summary>
public sealed class WorktreeInspectorTests
{
    private static readonly WorktreeSettings Relaxed = new() { PathBudgetReserve = 5, PathBudgetMargin = 2 };

    /// <summary>
    /// A <c>.git</c> file's gitdir line is read whole, as git reads it: a worktree moved by hand whose <c>.git</c> file
    /// names its record in five thousand characters - the record's path spelled with every <c>./</c> it may hold - is
    /// still known for the worktree git records elsewhere, never cut at a length of the code's own and read as no worktree.
    /// Red with the 4,096-character read put back.
    /// </summary>
    [Fact]
    public async Task AFiveThousandCharacterGitdir_IsReadWhole()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var created = await harness.WorktreeService.CreateAsync(temp.Path, "moved", useRandomName: false, cancellationToken);

        Assert.True(created.Succeeded, created.Outcome.Message);

        var record = (await harness.RunGitAsync(created.Path, ["rev-parse", "--absolute-git-dir"], cancellationToken)).StandardOutput.Trim();
        var root = Path.GetPathRoot(record)!;
        var spelled = Path.Combine(root, string.Concat(Enumerable.Repeat("./", 2500)), record[root.Length..]);
        var elsewhere = temp.Combine("elsewhere");

        Directory.Move(created.Path, elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, ".git"), $"gitdir: {spelled}\n");

        Assert.True(spelled.Length > 5000, $"the gitdir line is only {spelled.Length} characters");

        var moved = await Inspector(harness).MovedFromAsync(temp.Path, elsewhere, cancellationToken);

        Assert.NotNull(moved);
        PathAssert.Same(created.Path, moved);
    }

    /// <summary>
    /// The walk for a worktree's submodule repositories keeps the directories it has walked, by where their links lead, and
    /// ends at one it meets again: a modules directory holding a link back to itself, a level down, is walked once, and
    /// the look ends with what it found - never going round the link until a path grows too long to name. Red with the
    /// walk that keeps no visited set put back.
    /// </summary>
    [Fact]
    public async Task ASubmoduleWalk_EndsAtARevisitedDirectory()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var created = await harness.WorktreeService.CreateAsync(temp.Path, "looped", useRandomName: false, cancellationToken);

        Assert.True(created.Succeeded, created.Outcome.Message);

        var administrative = (await harness.RunGitAsync(created.Path, ["rev-parse", "--absolute-git-dir"], cancellationToken)).StandardOutput.Trim();
        var modules = Path.Combine(administrative, "modules");
        var inner = Directory.CreateDirectory(Path.Combine(modules, "lib", "inner")).FullName;
        var back = Path.Combine(inner, "back");

        TestLinks.DirectoryLink(back, modules);

        try
        {
            var inspector = Inspector(harness);
            var identity = await inspector.IdentifyAsync(temp.Path, WorktreeAddress.Plain("looped"), created.Path, cancellationToken);
            var findings = await inspector.FindAsync(temp.Path, created.Path, identity, cancellationToken);

            Assert.True(findings.HoldsSubmodules);
            Assert.Empty(findings.Submodules);
        }
        finally
        {
            // The link alone, never what it leads to.
            Directory.Delete(back);
        }
    }

    /// <summary>The inspector a deletion looks through, over the harness's own git and disk.</summary>
    private static WorktreeInspector Inspector(HarnessFactory harness)
        => new(harness.GitClient, harness.FileSystem, harness.Platform, harness.Output);

    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp)
    {
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken, new HarnessConfig { Worktrees = Relaxed });
        await harness.CommitAllAsync(temp.Path, "init", TestContext.Current.CancellationToken);

        return harness;
    }
}
