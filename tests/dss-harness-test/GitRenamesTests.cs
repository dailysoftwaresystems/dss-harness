using DssHarness.Core.Git;

namespace DssHarness.Tests;

/// <summary>
/// What the git client says of a file's renames: the name it had before each, newest first - so its history can be read
/// under every name it had.
/// </summary>
public sealed class GitRenamesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A file renamed twice is followed back across both renames, newest first, each naming the path the file had before
    /// it; an unrelated file that later took a former path is no part of it.
    /// </summary>
    [Fact]
    public async Task AFileRenamedTwice_IsFollowedBackAcrossBoth_NewestFirst()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);

        temp.WriteFile("arms.md", "A | one | x\n");
        await harness.CommitAllAsync(temp.Path, "the file", Token);

        Directory.CreateDirectory(temp.Combine("q"));
        await harness.RunGitAsync(temp.Path, ["mv", "arms.md", "q/arms.md"], Token);
        await harness.CommitAllAsync(temp.Path, "moved once", Token);

        Directory.CreateDirectory(temp.Combine("quality"));
        await harness.RunGitAsync(temp.Path, ["mv", "q/arms.md", "quality/arms.md"], Token);
        await harness.CommitAllAsync(temp.Path, "moved twice", Token);

        temp.WriteFile("arms.md", "something else entirely, at the first path again\n");
        await harness.CommitAllAsync(temp.Path, "another file", Token);

        var renames = await harness.GitClient.ListRenamesAsync(temp.Path, "HEAD", "quality/arms.md", Token);

        Assert.Equal([new GitRename("q/arms.md"), new GitRename("arms.md")], renames);
    }

    /// <summary>A file never renamed has no rename to follow.</summary>
    [Fact]
    public async Task AFileNeverRenamed_HasNone()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);

        temp.WriteFile("arms.md", "A | one | x\n");
        await harness.CommitAllAsync(temp.Path, "the file", Token);
        temp.WriteFile("arms.md", "A | two | x\n");
        await harness.CommitAllAsync(temp.Path, "changed", Token);

        Assert.Empty(await harness.GitClient.ListRenamesAsync(temp.Path, "HEAD", "arms.md", Token));
    }
}
