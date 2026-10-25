using DssHarness.Core.Ci;
using DssHarness.Core.Git;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// What the publishing steps run: the closed list of commands, each given the step's limit else its own and not done
/// past it; a forge's repository read from a remote's address; and the git writes a commit step publishes with, over a
/// real repository: staging by explicit path - each read literally,
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

        await harness.GitPublisher.StageAsync(temp.Path, ["[a].txt"], ["gone.txt", "never.txt"], null, Token);

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
        var publisher = harness.GitPublisher;
        var paths = Enumerable.Range(0, 450).Select(n => $"many/f{n:D3}.txt").ToList();

        foreach (var path in paths)
        {
            temp.WriteFile(path, path + "\n");
        }

        await publisher.StageAsync(temp.Path, paths, [], null, Token);

        Assert.Equal(450, (await StatusAsync(harness, temp)).Count(line => line.StartsWith("A  many/", StringComparison.Ordinal)));

        await publisher.UnstageAsync(temp.Path, paths, null, Token);

        Assert.Equal(450, (await StatusAsync(harness, temp)).Count(line => line.StartsWith("?? many/", StringComparison.Ordinal)));

        await publisher.StageAsync(temp.Path, paths, [], null, Token);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "many"], Token);
        Directory.Delete(temp.Combine("many"), recursive: true);

        await publisher.StageAsync(temp.Path, [], paths, null, Token);

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
        var publisher = harness.GitPublisher;
        temp.WriteFile("a.txt", "changed\n");
        temp.WriteFile("message.txt", "\nsubject\n\n\n\nthe body  \n\n");
        await publisher.StageAsync(temp.Path, ["a.txt"], [], null, Token);

        var signed = await publisher.CommitAsync(temp.Path, temp.Combine("message.txt"), signOff: true, null, Token);

        Assert.Equal(signed, await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", Token));
        Assert.Matches(@"\Asubject\n\nthe body\n\nSigned-off-by: [^\n]+\n\z", await MessageAsync(harness, temp));

        temp.WriteFile("a.txt", "again\n");
        await publisher.StageAsync(temp.Path, ["a.txt"], [], null, Token);
        _ = await publisher.CommitAsync(temp.Path, temp.Combine("message.txt"), signOff: false, null, Token);

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
        var publisher = harness.GitPublisher;
        temp.WriteFile("a.txt", "changed\n");
        temp.WriteFile("message.txt", "fix: the parser\n\n#42 is closed by it   \n\n\n");
        await publisher.StageAsync(temp.Path, ["a.txt"], [], null, Token);
        _ = await harness.RunGitAsync(temp.Path, ["config", "commit.cleanup", "strip"], Token);

        _ = await publisher.CommitAsync(temp.Path, temp.Combine("message.txt"), signOff: false, null, Token);

        Assert.Equal("fix: the parser\n\n#42 is closed by it\n", await MessageAsync(harness, temp));
    }

    /// <summary>git refusing to commit - nothing staged, a hook - is a failed command, said in git's own words.</summary>
    [Fact]
    public async Task GitRefusingToCommit_IsAFailedCommand_InItsOwnWords()
    {
        using var temp = new TempDirectory();
        var harness = await RepositoryAsync(temp);
        temp.WriteFile("message.txt", "subject\n");

        var refused = await Assert.ThrowsAsync<HarnessException>(() => harness.GitPublisher.CommitAsync(temp.Path, temp.Combine("message.txt"), signOff: false, null, Token));

        Assert.Equal(HarnessExit.CommandFailed, refused.ExitCode);
        Assert.StartsWith("git did not commit: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("nothing", refused.Message, StringComparison.Ordinal);

        var unstaged = await Assert.ThrowsAsync<HarnessException>(() => harness.GitPublisher.StageAsync(temp.Path, ["absent.txt"], [], null, Token));

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
        var publisher = harness.GitPublisher;
        temp.WriteFile("a.txt", "changed\n");
        temp.WriteFile("new.txt", "new\n");
        File.Delete(temp.Combine("gone.txt"));
        await publisher.StageAsync(temp.Path, ["a.txt", "new.txt"], ["gone.txt"], null, Token);

        await publisher.UnstageAsync(temp.Path, ["a.txt", "new.txt", "gone.txt"], null, Token);

        Assert.Equal([" M a.txt", " D gone.txt", "?? new.txt"], await StatusAsync(harness, temp));
        Assert.Equal("changed\n", File.ReadAllText(temp.Combine("a.txt")));

        using var empty = new TempDirectory();
        await harness.InitializeGitRepositoryAsync(empty.Path, Token);
        empty.WriteFile("first.txt", "first\n");
        await publisher.StageAsync(empty.Path, ["first.txt"], [], null, Token);

        await publisher.UnstageAsync(empty.Path, ["first.txt"], null, Token);

        Assert.Equal(["?? first.txt"], await StatusAsync(harness, empty));
    }

    /// <summary>
    /// Every git command the publisher runs is one of its closed list, begun with that command's own arguments - a push
    /// with one commit to one branch, never forced - and each is given the step's limit where the step says one, else its
    /// own: fifteen minutes for a commit's commands, five for a push.
    /// </summary>
    [Fact]
    public async Task EveryCommand_IsOneOfTheClosedList_GivenTheStepsLimitElseItsOwn()
    {
        var git = Substitute.For<IGitClient>();
        var asked = new List<(IReadOnlyList<string> Arguments, TimeSpan Limit)>();
        git.RunWithinAsync(default!, default!, default, Token).ReturnsForAnyArgs(call =>
        {
            var arguments = call.ArgAt<IReadOnlyList<string>>(1);
            asked.Add((arguments, call.ArgAt<TimeSpan>(2)));
            return Task.FromResult(new GitCommandResult(0, arguments.Contains("push") ? "To remote\n*\tabc:refs/heads/work\t[new branch]\nDone\n" : string.Empty, string.Empty));
        });
        git.ResolveCommitAsync(default!, default!, Token).ReturnsForAnyArgs(Task.FromResult<string?>("abc"));
        var publisher = new GitPublisher(git, new GhProcess(Substitute.For<IProcessRunner>(), GitLocalVariables.Fixed([]), _ => null));

        await publisher.StageAsync("/tree", ["a.txt"], ["gone.txt"], null, Token);
        await publisher.UnstageAsync("/tree", ["a.txt"], TimeSpan.FromMinutes(2), Token);
        _ = await publisher.CommitAsync("/tree", "/message.txt", signOff: true, null, Token);
        var pushed = await publisher.PushAsync("/tree", "origin", "abc", "work", null, Token);
        _ = await publisher.PushAsync("/tree", "origin", "abc", "work", TimeSpan.FromMinutes(2), Token);

        Assert.All(asked, call => Assert.Contains(
            PublisherCommands.All,
            command => command.Program == "git" && call.Arguments.Take(command.Leading.Count).SequenceEqual(command.Leading)));
        Assert.Equal(
            [PublisherCommands.CommitLimit, PublisherCommands.CommitLimit, TimeSpan.FromMinutes(2), PublisherCommands.CommitLimit, PublisherCommands.PushLimit, TimeSpan.FromMinutes(2)],
            asked.Select(call => call.Limit));
        Assert.Equal((TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5)), (PublisherCommands.CommitLimit, PublisherCommands.PushLimit));
        Assert.Equal(["git", "push", "--porcelain", "origin", "abc:refs/heads/work"], ["git", .. asked[^1].Arguments]);
        Assert.Equal(["/message.txt", "-s"], asked[3].Arguments.Skip(PublisherCommands.Commit.Leading.Count));
        Assert.True(pushed);
        Assert.Equal(7, PublisherCommands.All.Count);
    }

    /// <summary>
    /// A push reads the line of the branch it pushed, whatever else git pushed beside it - tags a configuration follows -
    /// and answers whether that branch moved; git saying it pushed and naming no line of the branch fails it (20).
    /// </summary>
    [Fact]
    public async Task APush_ReadsTheLineOfItsBranch_AndOneItDoesNotNameFails()
    {
        var git = Substitute.For<IGitClient>();
        git.RunWithinAsync(default!, default!, default, Token).ReturnsForAnyArgs(
            Task.FromResult(new GitCommandResult(0, "To remote\n*\trefs/tags/v1:refs/tags/v1\t[new tag]\n=\tabc:refs/heads/work\t[up to date]\nDone\n", string.Empty)),
            Task.FromResult(new GitCommandResult(0, "To remote\nDone\n", string.Empty)));
        var publisher = new GitPublisher(git, new GhProcess(Substitute.For<IProcessRunner>(), GitLocalVariables.Fixed([]), _ => null));

        var moved = await publisher.PushAsync("/tree", "origin", "abc", "work", null, Token);
        var unnamed = await Assert.ThrowsAsync<HarnessException>(() => publisher.PushAsync("/tree", "origin", "abc", "work", null, Token));

        Assert.False(moved);
        Assert.Equal((HarnessExit.CommandFailed, "git said it pushed abc to origin, and named no line of 'work' in what it pushed: To remote\nDone"), (unnamed.ExitCode, unnamed.Message));
    }

    /// <summary>
    /// A command that did not finish within its limit is not done (21), saying which, its limit and that what it may have
    /// written before it was ended is not known; asked again, it may finish.
    /// </summary>
    [Fact]
    public async Task ACommandPastItsLimit_IsNotDone_SayingWhatItLeftUndone()
    {
        var git = Substitute.For<IGitClient>();
        git.RunWithinAsync(default!, default!, default, Token).ReturnsForAnyArgs(Task.FromResult(new GitCommandResult(-1, string.Empty, string.Empty, TimedOut: true)));
        var publisher = new GitPublisher(git, new GhProcess(Substitute.For<IProcessRunner>(), GitLocalVariables.Fixed([]), _ => null));

        var push = await Assert.ThrowsAsync<HarnessException>(() => publisher.PushAsync("/tree", "origin", "abc", "work", TimeSpan.FromSeconds(90), Token));
        var commit = await Assert.ThrowsAsync<HarnessException>(() => publisher.CommitAsync("/tree", "/message.txt", signOff: false, null, Token));
        var stage = await Assert.ThrowsAsync<HarnessException>(() => publisher.StageAsync("/tree", ["a.txt"], [], null, Token));

        Assert.Equal((HarnessExit.Incomplete, "git push --porcelain did not finish within 90 s, so whether abc reached origin's 'work' is not known: asked again, it may finish."), (push.ExitCode, push.Message));
        Assert.Equal((HarnessExit.Incomplete, "git commit --cleanup=whitespace -F did not finish within 15 minutes, so whether it committed is not known: asked again, it may finish."), (commit.ExitCode, commit.Message));
        Assert.Equal((HarnessExit.Incomplete, "git --literal-pathspecs add -- did not finish within 15 minutes, so which of 1 path(s) git got to stage is not known: asked again, it may finish."), (stage.ExitCode, stage.Message));
    }

    /// <summary>
    /// The repository on a forge a remote's address names, as the forge's tool takes it - host, owner and name, the user
    /// and .git left out, and the port but a web address's - of every form git writes one in, an address over ssh said
    /// so; none of a path on this machine, a file address, a path from an ssh server's root, or an address of more or
    /// fewer parts than an owner and a name.
    /// </summary>
    [Theory]
    [InlineData("https://github.com/acme/widgets.git", "github.com/acme/widgets", false)]
    [InlineData("https://user@github.example.com:8443/acme/widgets/", "github.example.com:8443/acme/widgets", false)]
    [InlineData("http://[::1]:8080/acme/widgets", "[::1]:8080/acme/widgets", false)]
    [InlineData("ssh://git@github.com:22/acme/widgets.git", "github.com/acme/widgets", true)]
    [InlineData("ssh://git@[::1]:2222/acme/widgets", "[::1]/acme/widgets", true)]
    [InlineData("git://github.com:9418/acme/widgets", "github.com/acme/widgets", false)]
    [InlineData("git@github.com:acme/widgets.git", "github.com/acme/widgets", true)]
    [InlineData("work-gh:acme/widgets", "work-gh/acme/widgets", true)]
    [InlineData("git@server:/acme/widgets", null, false)]
    [InlineData("C:acme/widgets", null, false)]
    [InlineData("https://github.com:x/acme/widgets", null, false)]
    [InlineData("git@[::1]:acme/widgets", null, false)]
    [InlineData("https://[::1/acme/widgets", null, false)]
    [InlineData("C:\\repos\\widgets", null, false)]
    [InlineData("C:/repos/acme/widgets", null, false)]
    [InlineData("C:/acme/widgets", null, false)]
    [InlineData("/srv/git:acme/widgets", null, false)]
    [InlineData("ftps://example.com/acme/widgets.git", null, false)]
    [InlineData("/srv/git/acme/widgets.git", null, false)]
    [InlineData("../acme/widgets", null, false)]
    [InlineData("file:///srv/acme/widgets.git", null, false)]
    [InlineData("https://dev.azure.com/acme/project/_git/widgets", null, false)]
    [InlineData("https://github.com/acme", null, false)]
    [InlineData("https://github.com/acme/.git", null, false)]
    public void AForgesRepository_IsReadFromItsRemotesAddress(string url, string? repository, bool overSsh)
    {
        var address = GitRemotes.ForgeAddress(url);

        Assert.Equal((repository, overSsh), (address?.Repository, address?.OverSsh ?? false));
    }

    /// <summary>
    /// The host an address over ssh reaches is the one ssh's configuration names it, read as ssh would dial it and
    /// dialing nothing - a forge's ssh-only host named as the forge's own, as its tool names it; none where ssh is not
    /// here, did not say, or the host would read as an option of ssh's.
    /// </summary>
    [Fact]
    public async Task AHostOverSsh_IsTheOneSshsConfigurationNamesIt()
    {
        var requests = new List<ProcessRequest>();
        var runner = Substitute.For<IProcessRunner>();
        runner.FindExecutable("ssh").Returns("ssh");
        runner.RunAsync(Arg.Do<ProcessRequest>(requests.Add), Token).ReturnsForAnyArgs(
            Task.FromResult(new ProcessResult(0, "user git\r\nhostname github.example.com\r\nport 22\r\n", string.Empty, TimeSpan.Zero, TimedOut: false)),
            Task.FromResult(new ProcessResult(0, "user git\nhostname SSH.github.com\nport 443\n", string.Empty, TimeSpan.Zero, TimedOut: false)),
            Task.FromResult(new ProcessResult(255, string.Empty, "Bad configuration option", TimeSpan.Zero, TimedOut: false)));
        var hosts = new SshHostNames(runner);

        var aliased = await hosts.HostNameAsync("work-gh", Token);
        var throughTheWebsPort = await hosts.HostNameAsync("github.com", Token);
        var unsaid = await hosts.HostNameAsync("work-gh", Token);
        var option = await hosts.HostNameAsync("-oProxyCommand=x", Token);
        runner.FindExecutable("ssh").Returns((string?)null);
        var noSsh = await hosts.HostNameAsync("work-gh", Token);

        Assert.Equal(("github.example.com", "github.com", null, null, null), (aliased, throughTheWebsPort, unsaid, option, noSsh));
        Assert.Equal(3, requests.Count);
        Assert.Equal(["-G", "work-gh"], requests[0].Arguments);
        Assert.Equal(SshHostNames.Limit, requests[0].Timeout);
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
