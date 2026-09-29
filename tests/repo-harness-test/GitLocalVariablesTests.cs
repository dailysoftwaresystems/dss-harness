using NSubstitute;
using RepoHarness.Core.Ci;
using RepoHarness.Core.Git;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>Which variables git reads a repository from, and the children that start without them.</summary>
public sealed class GitLocalVariablesTests
{
    /// <summary>
    /// git is asked, and once: the list is git's own, and was written down as three names while git gives
    /// fifteen, GIT_COMMON_DIR among them.
    /// </summary>
    [Fact]
    public async Task NamesAsync_AsksGitOnce_ForEveryVariableARepositoryIsReadFrom()
    {
        var harness = new HarnessFactory();
        var processes = new CountingProcesses(harness.ProcessRunner);
        var variables = new GitLocalVariables(processes);
        var cancellationToken = TestContext.Current.CancellationToken;

        var names = await variables.NamesAsync(cancellationToken);
        var again = await variables.NamesAsync(cancellationToken);

        Assert.Same(names, again);
        Assert.Single(processes.Started);
        Assert.Superset(
            new HashSet<string>(["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT"], StringComparer.Ordinal),
            new HashSet<string>(names, StringComparer.Ordinal));
    }

    /// <summary>
    /// A list without GIT_DIR, the variable every git hook runs with, is not taken, and nothing is run with
    /// it; and a question that failed is asked again rather than kept as the answer.
    /// </summary>
    [Fact]
    public async Task AnAnswerThatDoesNotNameGitDir_IsRefused_AndAskedAgainNextTime()
    {
        var answers = new Queue<ProcessResult>([Exited(0, "GIT_WORK_TREE\nGIT_INDEX_FILE\n"), Exited(0, "GIT_DIR\r\nGIT_COMMON_DIR\r\n")]);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>()).Returns(_ => answers.Dequeue());
        var variables = new GitLocalVariables(runner);
        var cancellationToken = TestContext.Current.CancellationToken;

        var refused = await Assert.ThrowsAsync<HarnessException>(() => variables.NamesAsync(cancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refused.ExitCode);
        Assert.Equal(
            "git could not name the variables it reads a repository from ('git rev-parse --local-env-vars': its answer did not name GIT_DIR), "
            + "so nothing was run: a git command started with one of them set could answer for a repository nobody named.",
            refused.Message);
        Assert.Equal(["GIT_DIR", "GIT_COMMON_DIR"], await variables.NamesAsync(cancellationToken));
    }

    /// <summary>git failing to answer is said with what it said, and fails the command rather than running git unguarded.</summary>
    [Fact]
    public async Task AQuestionGitFails_SaysWhatGitSaid()
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>()).Returns(Exited(129, stderr: "error: unknown option\n"));

        var refused = await Assert.ThrowsAsync<HarnessException>(
            () => new GitLocalVariables(runner).NamesAsync(TestContext.Current.CancellationToken));

        Assert.Contains("('git rev-parse --local-env-vars': it exited 129: error: unknown option)", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The forge's command line runs git to find its repository, so it starts without the same variables:
    /// with another repository's GIT_DIR exported it answered for that repository.
    /// </summary>
    [Fact]
    public async Task TheForgesCommandLine_StartsWithoutEveryVariableGitReadsARepositoryFrom()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("GH_REPO") is { Length: > 0 },
            "GH_REPO is set in this environment, which the command refuses before it starts anything.");

        var requests = new List<ProcessRequest>();
        var runner = Substitute.For<IProcessRunner>();
        runner.FindExecutable("gh").Returns("gh");
        runner.RunAsync(Arg.Do<ProcessRequest>(requests.Add), Arg.Any<CancellationToken>()).Returns(Exited(0, "[{\"databaseId\": 7}]"));
        string[] names = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY"];

        var runs = await new GhCiJobSource(runner, GitLocalVariables.Fixed(names))
            .ListRunsAsync("/repo", "test.yml", "main", 1, TestContext.Current.CancellationToken);

        Assert.Equal([7L], runs);
        var environment = Assert.Single(requests).Environment;
        Assert.All(names, name => Assert.True(environment.TryGetValue(name, out var value) && value is null, $"{name} was not cleared."));
    }

    private static ProcessResult Exited(int exitCode, string standardOutput = "", string stderr = "")
        => new(exitCode, standardOutput, stderr, TimeSpan.Zero, TimedOut: false);
}
