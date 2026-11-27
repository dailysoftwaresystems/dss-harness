using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// The directories of the one tree a run reads its lists of paths against (<see cref="TreeDirectories"/>): a commit's, as
/// git lists it, the same answer whatever the working tree holds since; and a tree that cannot be read answering by what is
/// asked of it.
/// </summary>
public sealed class TreeDirectoriesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A commit's directories are every tree it holds and every submodule's entry, which a work tree holds as a directory -
    /// never a file, nor a symbolic link, which git keeps as a file whatever it leads to - and they are the commit's: a
    /// directory the working tree made since, or took away, changes nothing, and names compare by the case rule given.
    /// </summary>
    [Fact]
    public async Task ACommitsDirectories_AreItsTreesAndSubmodules_WhateverTheWorkingTreeHoldsSince()
    {
        using var repository = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(repository.Path, Token);
        repository.WriteFile("src/click/types.py", "types\n");
        repository.WriteFile("target.txt", "where the link leads\n");
        await harness.CommitAllAsync(repository.Path, "the tree", Token);

        // A link and a submodule's entry, put in the index as git keeps them, so every system holds them alike.
        var blob = (await harness.RunGitAsync(repository.Path, ["rev-parse", "HEAD:target.txt"], Token)).StandardOutput.Trim();
        var head = (await harness.RunGitAsync(repository.Path, ["rev-parse", "HEAD"], Token)).StandardOutput.Trim();
        await harness.RunGitAsync(repository.Path, ["update-index", "--add", "--cacheinfo", $"120000,{blob},linked"], Token);
        await harness.RunGitAsync(repository.Path, ["update-index", "--add", "--cacheinfo", $"160000,{head},vendor/sub"], Token);
        await harness.RunGitAsync(repository.Path, ["commit", "--quiet", "-m", "a link and a submodule"], Token);
        var start = (await harness.RunGitAsync(repository.Path, ["rev-parse", "HEAD"], Token)).StandardOutput.Trim();

        // The working tree moves on: a directory made, one taken away.
        Directory.CreateDirectory(Path.Combine(repository.Path, "docs", "api"));
        Directory.Delete(Path.Combine(repository.Path, "src"), recursive: true);

        var tree = await TreeDirectories.AtAsync(harness.GitClient, repository.Path, start, StringComparison.Ordinal, Token);

        Assert.Equal((start, (string?)null), (tree.Commit, tree.Unread));

        foreach (var directory in new[] { "src", "src/click", "vendor", "vendor/sub" })
        {
            Assert.True(tree.Meeting(directory), directory);
            Assert.True(tree.Writing(directory), directory);
        }

        foreach (var none in new[] { "linked", "target.txt", "README.md", "src/click/types.py", "docs", "docs/api", "SRC" })
        {
            Assert.False(tree.Meeting(none), none);
            Assert.False(tree.Writing(none), none);
        }

        var folding = await TreeDirectories.AtAsync(harness.GitClient, repository.Path, start, StringComparison.OrdinalIgnoreCase, Token);

        Assert.True(folding.Writing("SRC/Click"));
    }

    /// <summary>
    /// A tree that cannot be read - the run names no commit it started from, or git cannot list the one it names - answers
    /// by the question: every path counts as a directory where two entries are weighed for meeting, none where it is what a
    /// step may write or a fold carry; and it says why it could not be read.
    /// </summary>
    [Fact]
    public async Task ATreeThatCannotBeRead_CountsEveryPathADirectoryForMeeting_AndNoneForWriting()
    {
        using var repository = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(repository.Path, Token);
        var missing = new string('0', 40);

        var none = await TreeDirectories.AtAsync(harness.GitClient, repository.Path, commit: null, StringComparison.Ordinal, Token);
        var unread = await TreeDirectories.AtAsync(harness.GitClient, repository.Path, missing, StringComparison.Ordinal, Token);

        Assert.Same(TreeDirectories.NoCommit, none);
        Assert.Equal(((string?)null, "the run names no commit it started from"), (none.Commit, none.Unread));
        Assert.Equal(missing, unread.Commit);
        Assert.StartsWith($"git could not list the directories of '{missing}': ", unread.Unread, StringComparison.Ordinal);

        foreach (var tree in new[] { none, unread })
        {
            Assert.True(tree.Meeting("README.md"));
            Assert.True(tree.Meeting("docs/api"));
            Assert.False(tree.Writing("README.md"));
            Assert.False(tree.Writing("docs/api"));
        }
    }
}
