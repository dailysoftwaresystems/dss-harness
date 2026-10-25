using DssHarness.Core.Git;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The git writes a commit step publishes with, over a real repository: staging by explicit path - each read literally,
/// in as many runs as the command line holds, a removal among them and nothing else - one commit with its message and a
/// sign-off where asked, answering the commit made; unstaging back to HEAD, or out of the index where there is no commit
/// yet; and git's refusal said with its own words as a failed command.
/// </summary>
public sealed class GitPublisherTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// What is staged is each path named, read literally - a name that would be a pattern stages that file alone - and the
    /// removal of each path named deleted, one the index does not hold left as it is; what is not named stays unstaged.
    /// </summary>
    [Fact]
    public async Task Staging_TakesEachPathNamedLiterally_AndNothingElse()
    {
        using var temp = new TempDirectory();
        var harness = await RepositoryAsync(temp);
        temp.WriteFile("[a].txt", "pattern\n");
        temp.WriteFile("a.txt", "changed\n");
        temp.WriteFile("other.txt", "not named\n");
        File.Delete(temp.Combine("gone.txt"));

        await new GitPublisher(harness.GitClient).StageAsync(temp.Path, ["[a].txt"], ["gone.txt", "never.txt"], Token);

        Assert.Equal(["A  [a].txt", " M a.txt", "D  gone.txt", "?? other.txt"], await StatusAsync(harness, temp));
    }

    /// <summary>
    /// More paths than one command line holds are staged, unstaged and staged as removed in as many runs as it takes,
    /// every one of them.
    /// </summary>
    [Fact]
    public async Task ManyPaths_AreStagedAndUnstagedInBatches_EveryOne()
    {
        using var temp = new TempDirectory();
        var harness = await RepositoryAsync(temp);
        var publisher = new GitPublisher(harness.GitClient);
        var paths = Enumerable.Range(0, 450).Select(n => $"many/f{n:D3}.txt").ToList();

        foreach (var path in paths)
        {
            temp.WriteFile(path, path + "\n");
        }

        await publisher.StageAsync(temp.Path, paths, [], Token);

        Assert.Equal(450, (await StatusAsync(harness, temp)).Count(line => line.StartsWith("A  many/", StringComparison.Ordinal)));

        await publisher.UnstageAsync(temp.Path, paths, Token);

        Assert.Equal(450, (await StatusAsync(harness, temp)).Count(line => line.StartsWith("?? many/", StringComparison.Ordinal)));

        await publisher.StageAsync(temp.Path, paths, [], Token);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "many"], Token);
        Directory.Delete(temp.Combine("many"), recursive: true);

        await publisher.StageAsync(temp.Path, [], paths, Token);

        Assert.Equal(450, (await StatusAsync(harness, temp)).Count(line => line.StartsWith("D  many/", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A commit takes what the index holds, with the message as the file holds it - blank lines at its ends and trailing
    /// blanks dropped, a run of blank lines made one - a sign-off where asked and none where not, and answers the commit it
    /// made.
    /// </summary>
    [Fact]
    public async Task ACommit_TakesTheIndexWithItsMessage_AndAnswersTheCommitMade()
    {
        using var temp = new TempDirectory();
        var harness = await RepositoryAsync(temp);
        var publisher = new GitPublisher(harness.GitClient);
        temp.WriteFile("a.txt", "changed\n");
        temp.WriteFile("message.txt", "\nsubject\n\n\n\nthe body  \n\n");
        await publisher.StageAsync(temp.Path, ["a.txt"], [], Token);

        var signed = await publisher.CommitAsync(temp.Path, temp.Combine("message.txt"), signOff: true, Token);

        Assert.Equal(signed, await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", Token));
        Assert.Matches(@"\Asubject\n\nthe body\n\nSigned-off-by: [^\n]+\n\z", await MessageAsync(harness, temp));

        temp.WriteFile("a.txt", "again\n");
        await publisher.StageAsync(temp.Path, ["a.txt"], [], Token);
        _ = await publisher.CommitAsync(temp.Path, temp.Combine("message.txt"), signOff: false, Token);

        Assert.DoesNotContain("Signed-off-by", await MessageAsync(harness, temp), StringComparison.Ordinal);
    }

    /// <summary>
    /// A message's lines that begin with # are its own, kept as written - git drops only blank lines and trailing blanks
    /// from it - even where the repository's configuration would have git strip them as comments.
    /// </summary>
    [Fact]
    public async Task ACommitsMessage_KeepsItsLinesThatBeginWithAHash()
    {
        using var temp = new TempDirectory();
        var harness = await RepositoryAsync(temp);
        var publisher = new GitPublisher(harness.GitClient);
        temp.WriteFile("a.txt", "changed\n");
        temp.WriteFile("message.txt", "fix: the parser\n\n#42 is closed by it   \n\n\n");
        await publisher.StageAsync(temp.Path, ["a.txt"], [], Token);
        _ = await harness.RunGitAsync(temp.Path, ["config", "commit.cleanup", "strip"], Token);

        _ = await publisher.CommitAsync(temp.Path, temp.Combine("message.txt"), signOff: false, Token);

        Assert.Equal("fix: the parser\n\n#42 is closed by it\n", await MessageAsync(harness, temp));
    }

    /// <summary>git refusing to commit - nothing staged, a hook - is a failed command, said in git's own words.</summary>
    [Fact]
    public async Task GitRefusingToCommit_IsAFailedCommand_InItsOwnWords()
    {
        using var temp = new TempDirectory();
        var harness = await RepositoryAsync(temp);
        temp.WriteFile("message.txt", "subject\n");

        var refused = await Assert.ThrowsAsync<HarnessException>(() => new GitPublisher(harness.GitClient).CommitAsync(temp.Path, temp.Combine("message.txt"), signOff: false, Token));

        Assert.Equal(HarnessExit.CommandFailed, refused.ExitCode);
        Assert.StartsWith("git did not commit: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("nothing", refused.Message, StringComparison.Ordinal);

        var unstaged = await Assert.ThrowsAsync<HarnessException>(() => new GitPublisher(harness.GitClient).StageAsync(temp.Path, ["absent.txt"], [], Token));

        Assert.Equal(HarnessExit.CommandFailed, unstaged.ExitCode);
        Assert.StartsWith("git could not stage 1 path(s): ", unstaged.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Unstaging puts the index back at each path as HEAD holds it, leaving the work tree as it is - a removal staged
    /// among it - and, on a branch with no commit yet, takes each path out of the index.
    /// </summary>
    [Fact]
    public async Task Unstaging_PutsTheIndexBackAtHead_OrOutOfTheIndexWhereThereIsNone()
    {
        using var temp = new TempDirectory();
        var harness = await RepositoryAsync(temp);
        var publisher = new GitPublisher(harness.GitClient);
        temp.WriteFile("a.txt", "changed\n");
        temp.WriteFile("new.txt", "new\n");
        File.Delete(temp.Combine("gone.txt"));
        await publisher.StageAsync(temp.Path, ["a.txt", "new.txt"], ["gone.txt"], Token);

        await publisher.UnstageAsync(temp.Path, ["a.txt", "new.txt", "gone.txt"], Token);

        Assert.Equal([" M a.txt", " D gone.txt", "?? new.txt"], await StatusAsync(harness, temp));
        Assert.Equal("changed\n", File.ReadAllText(temp.Combine("a.txt")));

        using var empty = new TempDirectory();
        await harness.InitializeGitRepositoryAsync(empty.Path, Token);
        empty.WriteFile("first.txt", "first\n");
        await publisher.StageAsync(empty.Path, ["first.txt"], [], Token);

        await publisher.UnstageAsync(empty.Path, ["first.txt"], Token);

        Assert.Equal(["?? first.txt"], await StatusAsync(harness, empty));
    }

    /// <summary>A repository with <c>a.txt</c> and <c>gone.txt</c> committed.</summary>
    private static async Task<HarnessFactory> RepositoryAsync(TempDirectory temp)
    {
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        temp.WriteFile("a.txt", "one\n");
        temp.WriteFile("gone.txt", "two\n");
        await harness.CommitAllAsync(temp.Path, "files", Token);
        return harness;
    }

    /// <summary>The repository's status, a line each as <c>git status --porcelain</c> prints it, in git's order.</summary>
    private static async Task<IReadOnlyList<string>> StatusAsync(HarnessFactory harness, TempDirectory temp)
        => [.. (await harness.RunGitAsync(temp.Path, ["status", "--porcelain", "--untracked-files=all"], Token)).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.EndsWith("message.txt", StringComparison.Ordinal))];

    /// <summary>The message of HEAD, whole.</summary>
    private static async Task<string> MessageAsync(HarnessFactory harness, TempDirectory temp)
        => (await harness.RunGitAsync(temp.Path, ["log", "-1", "--format=%B"], Token)).StandardOutput.TrimEnd('\n') + "\n";
}
