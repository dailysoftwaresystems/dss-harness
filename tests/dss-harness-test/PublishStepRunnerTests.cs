using System.Text.Json.Nodes;
using DssHarness.Core.Ci;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Schemas;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// The push and pull-request steps over a real repository: a push sends the main checkout's HEAD to its branch on the
/// remote - the one the step names, else the one the run's branch goes to - never forced, answering whether the remote's
/// branch moved, and refuses a detached HEAD and a remote it cannot name; a pull request of that branch is found where
/// one is open and opened where none is, on the repository the remote's address names, against the base the step names
/// or the remote's default, its body written for the forge's tool to read - the forge itself scripted.
/// </summary>
public sealed class PublishStepRunnerTests
{
    private static readonly WorkflowDefinition Definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
        name: flow
        steps:
          - name: push
            type: push
          - name: pr
            type: pull-request
            title: the run's work
            body: body.md
            dependsOn: [push]
        """);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A push sends the commit HEAD names to the branch of its name on the remote the run's branch goes to and answers
    /// that it moved it; run again, it pushes what is pushed already and answers that it did not.
    /// </summary>
    [Fact]
    public async Task APush_SendsHeadToItsBranch_AndRunAgainMovesNothing()
    {
        using var temp = new TempDirectory();
        using var remote = new TempDirectory();
        var kit = await PushingToAsync(temp, remote);

        var first = await Push(kit).RunAsync(Run(kit, PushStep()), Token);
        var again = await Push(kit).RunAsync(Run(kit, PushStep()), Token);

        var head = await HeadAsync(kit);
        Assert.True(first.Passed, first.Error);
        Assert.Equal($$"""{"pushed":true,"remote":"origin","branch":"work","sha":"{{head}}"}""", first.Output!.ToJsonString());
        Assert.Equal($$"""{"pushed":false,"remote":"origin","branch":"work","sha":"{{head}}"}""", again.Output!.ToJsonString());
        Assert.Equal(head, (await kit.Harness.RunGitAsync(remote.Path, ["rev-parse", "refs/heads/work"], Token)).StandardOutput.Trim());
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("push")!.Output!)!, first.Output));

        // A commit after it is a fast-forward the remote's branch moves on by.
        await kit.Harness.RunGitAsync(kit.Main, ["commit", "--quiet", "--allow-empty", "-m", "more"], Token);
        var onward = await Push(kit).RunAsync(Run(kit, PushStep()), Token);

        Assert.Equal($$"""{"pushed":true,"remote":"origin","branch":"work","sha":"{{await HeadAsync(kit)}}"}""", onward.Output!.ToJsonString());
    }

    /// <summary>A push goes to the remote the step names, where it names one, not to the one the run's branch goes to.</summary>
    [Fact]
    public async Task APush_GoesToTheRemoteTheStepNames()
    {
        using var temp = new TempDirectory();
        using var remote = new TempDirectory();
        using var other = new TempDirectory();
        var kit = await PushingToAsync(temp, remote);
        await kit.Harness.RunGitAsync(other.Path, ["init", "--bare", "--quiet", "."], Token);
        await kit.Harness.RunGitAsync(kit.Main, ["remote", "add", "mirror", other.Path], Token);

        var pushed = await Push(kit).RunAsync(Run(kit, PushStep() with { Remote = "mirror" }), Token);

        Assert.Equal("mirror", pushed.Output!["remote"]!.GetValue<string>());
        Assert.Equal(await HeadAsync(kit), (await kit.Harness.RunGitAsync(other.Path, ["rev-parse", "refs/heads/work"], Token)).StandardOutput.Trim());
        Assert.False((await kit.Harness.GitClient.RunAsync(remote.Path, ["rev-parse", "--verify", "--quiet", "refs/heads/work"], cancellationToken: Token)).Succeeded);
    }

    /// <summary>
    /// A push the remote refuses - its branch moved on since - fails the step (20) in git's own words, and the remote's
    /// branch stays where it was: nothing is forced.
    /// </summary>
    [Fact]
    public async Task APushTheRemoteRefuses_FailsTheStep_AndForcesNothing()
    {
        using var temp = new TempDirectory();
        using var remote = new TempDirectory();
        var kit = await PushingToAsync(temp, remote);
        _ = await Push(kit).RunAsync(Run(kit, PushStep()), Token);
        await kit.Harness.RunGitAsync(kit.Main, ["checkout", "--quiet", "-b", "theirs"], Token);
        await kit.Harness.RunGitAsync(kit.Main, ["commit", "--quiet", "--allow-empty", "-m", "theirs"], Token);
        await kit.Harness.RunGitAsync(kit.Main, ["push", "--quiet", "--force", "origin", "theirs:refs/heads/work"], Token);
        var theirs = await HeadAsync(kit);
        await kit.Harness.RunGitAsync(kit.Main, ["checkout", "--quiet", "work"], Token);
        await kit.Harness.RunGitAsync(kit.Main, ["commit", "--quiet", "--allow-empty", "-m", "ours"], Token);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => Push(kit).RunAsync(Run(kit, PushStep()), Token));

        Assert.Equal(HarnessExit.CommandFailed, refused.ExitCode);
        Assert.StartsWith($"git did not push {await HeadAsync(kit)} to origin's 'work': [rejected]", refused.Message, StringComparison.Ordinal);
        Assert.Equal(theirs, (await kit.Harness.RunGitAsync(remote.Path, ["rev-parse", "refs/heads/work"], Token)).StandardOutput.Trim());
    }

    /// <summary>
    /// The step's own remote is the one pushed to; one the repository does not have, none that is the one the run's
    /// branch goes to, no remote at all, a branch with no commit yet and a detached HEAD are each refused (13), and
    /// nothing is pushed.
    /// </summary>
    [Fact]
    public async Task APushWithNoRemoteItCanName_OrFromADetachedHead_IsRefused_NothingPushed()
    {
        using var temp = new TempDirectory();
        using var remote = new TempDirectory();
        var kit = await PushingToAsync(temp, remote);
        var publisher = Substitute.For<IGitPublisher>();

        var unknown = await Push(kit, publisher).RunAsync(Run(kit, PushStep() with { Remote = "elsewhere" }), Token);
        await kit.Harness.RunGitAsync(kit.Main, ["remote", "rename", "origin", "one"], Token);
        await kit.Harness.RunGitAsync(kit.Main, ["remote", "add", "two", remote.Path], Token);
        var unnamed = await Push(kit, publisher).RunAsync(Run(kit, PushStep()), Token);
        await kit.Harness.RunGitAsync(kit.Main, ["remote", "remove", "one"], Token);
        await kit.Harness.RunGitAsync(kit.Main, ["remote", "remove", "two"], Token);
        var none = await Push(kit, publisher).RunAsync(Run(kit, PushStep()), Token);
        await kit.Harness.RunGitAsync(kit.Main, ["remote", "add", "origin", remote.Path], Token);
        await kit.Harness.RunGitAsync(kit.Main, ["checkout", "--quiet", "--orphan", "fresh"], Token);
        var unborn = await Push(kit, publisher).RunAsync(Run(kit, PushStep()), Token);
        await kit.Harness.RunGitAsync(kit.Main, ["checkout", "--quiet", "--force", "work"], Token);
        await kit.Harness.RunGitAsync(kit.Main, ["checkout", "--quiet", "--detach"], Token);
        var detached = await Push(kit, publisher).RunAsync(Run(kit, PushStep()), Token);

        Assert.All([unknown, unnamed, none, unborn, detached], refused => Assert.Equal((false, HarnessExit.Refused), (refused.Passed, refused.Exit)));
        Assert.Equal("'push' pushes 'fresh', which holds no commit yet. Nothing was pushed.", unborn.Error);
        Assert.Equal("'push' pushes to 'elsewhere', which is not a remote of this repository (origin): name one of them, or add it. Nothing was pushed.", unknown.Error);
        Assert.Equal(
            "'push' pushes to the remote the run's branch goes to, and none is that one (the push step names none, the branch tracks none, and none is origin): name one on the step. Nothing was pushed.",
            unnamed.Error);
        Assert.Equal("'push' pushes to the remote the run's branch goes to, and the repository has no remote: name one on the step. Nothing was pushed.", none.Error);
        Assert.Equal("'push' pushes the branch the main checkout is on, and its HEAD is detached: check out the run's branch, then resume. Nothing was pushed.", detached.Error);
        Assert.Empty(publisher.ReceivedCalls());
    }

    /// <summary>A push is given the step's time limit where it says one, and its own where it does not.</summary>
    [Fact]
    public async Task APush_IsGivenTheStepsLimit_ElseItsOwn()
    {
        using var temp = new TempDirectory();
        using var remote = new TempDirectory();
        var kit = await PushingToAsync(temp, remote);
        var publisher = Substitute.For<IGitPublisher>();

        _ = await Push(kit, publisher).RunAsync(Run(kit, PushStep() with { TimeoutMinutes = 3 }), Token);
        _ = await Push(kit, publisher).RunAsync(Run(kit, PushStep()), Token);

        var head = await HeadAsync(kit);
        await publisher.Received(1).PushAsync(kit.Main, "origin", head, "work", TimeSpan.FromMinutes(3), Arg.Any<CancellationToken>());
        await publisher.Received(1).PushAsync(kit.Main, "origin", head, "work", null, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// With none open, a pull request is opened from the run's branch to the remote's default branch on the repository
    /// the remote's address names - its title the step's, its body the text the engine filled, written to the step's
    /// directory for the forge's tool to read - and the step answers its number and address, created; each call of the
    /// forge given the step's limit, else its own, and a draft opened as one.
    /// </summary>
    [Fact]
    public async Task APullRequest_IsOpenedWhereNoneIsOpen_OnTheRemotesRepository_AgainstItsDefault()
    {
        using var temp = new TempDirectory();
        var kit = await ForgedAsync(temp);
        var requests = new List<ProcessRequest>();
        var created = Exited(0, "Creating pull request for work into main\n\nhttps://github.com/acme/widgets/pull/42\n");
        var runner = PullRequests(kit, requests, null, Exited(0, "[]\n"), created, Exited(0, "[]\n"), created);

        var opened = await runner.RunAsync(Run(kit, PullRequestStep(), body: "the body\n"), Token);
        _ = await runner.RunAsync(Run(kit, PullRequestStep() with { Draft = true, TimeoutMinutes = 2 }, body: "the body\n"), Token);

        var bodyFile = kit.Layout.InstancePullRequestBodyFile("pr");
        Assert.True(opened.Passed, opened.Error);
        Assert.Equal("""{"number":42,"url":"https://github.com/acme/widgets/pull/42","created":true}""", opened.Output!.ToJsonString());
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("pull-request")!.Output!)!, opened.Output));
        Assert.Equal(
            [
                ["pr", "list", "--state", "open", "--json", "number,url", "--limit", "1", "--repo", "github.com/acme/widgets", "--base", "main", "--head", "work"],
                ["pr", "create", "--repo", "github.com/acme/widgets", "--base", "main", "--head", "work", "--title", "the run's work", "--body-file", bodyFile],
                ["pr", "list", "--state", "open", "--json", "number,url", "--limit", "1", "--repo", "github.com/acme/widgets", "--base", "main", "--head", "work"],
                ["pr", "create", "--repo", "github.com/acme/widgets", "--base", "main", "--head", "work", "--title", "the run's work", "--body-file", bodyFile, "--draft"],
            ],
            requests.Select(request => request.Arguments.ToArray()));
        Assert.All(requests, request => Assert.Contains(
            PublisherCommands.All,
            command => command.Program == GhProcess.Executable && request.Arguments.Take(command.Leading.Count).SequenceEqual(command.Leading)));
        Assert.Equal([GhProcess.Limit, GhProcess.Limit, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2)], requests.Select(request => request.Timeout));
        Assert.All(requests, request => Assert.Equal(kit.Main, request.WorkingDirectory));
        Assert.Equal("the body\n", File.ReadAllText(bodyFile));
    }

    /// <summary>
    /// One already open from the run's branch to its base - the step's own here - is answered as it is, not created, and
    /// nothing is opened or written: its title and body are a person's now.
    /// </summary>
    [Fact]
    public async Task APullRequestAlreadyOpen_IsAnsweredAsItIs_AndNoneIsOpened()
    {
        using var temp = new TempDirectory();
        var kit = await ForgedAsync(temp);
        var requests = new List<ProcessRequest>();
        var runner = PullRequests(kit, requests, null, Exited(0, """[{"number":7,"url":"https://github.com/acme/widgets/pull/7"}]"""));

        var found = await runner.RunAsync(Run(kit, PullRequestStep() with { Base = "release" }, body: "the body\n"), Token);

        Assert.True(found.Passed, found.Error);
        Assert.Equal("""{"number":7,"url":"https://github.com/acme/widgets/pull/7","created":false}""", found.Output!.ToJsonString());
        Assert.Equal(["--base", "release", "--head", "work"], Assert.Single(requests).Arguments.TakeLast(4));
        Assert.False(File.Exists(kit.Layout.InstancePullRequestBodyFile("pr")));
    }

    /// <summary>
    /// A title blank once filled fails the step (12); HEAD detached, no remote, a remote the push step names that the
    /// repository does not have, no base it can tell and a base that is the branch itself each refuse it (13); and the
    /// forge is never asked.
    /// </summary>
    [Fact]
    public async Task APullRequestItCannotPlace_IsRefused_AndTheForgeIsNeverAsked()
    {
        using var temp = new TempDirectory();
        var kit = await ForgedAsync(temp);
        var requests = new List<ProcessRequest>();
        var runner = PullRequests(kit, requests, null, Exited(0, "[]"));

        var blank = await runner.RunAsync(Run(kit, PullRequestStep() with { Title = "  " }, body: "the body\n"), Token);
        var elsewhere = await runner.RunAsync(Run(kit, PullRequestStep(), body: "the body\n", definition: PushingTo("elsewhere")), Token);
        var itself = await runner.RunAsync(Run(kit, PullRequestStep() with { Base = "work" }, body: "the body\n"), Token);
        await kit.Harness.RunGitAsync(kit.Main, ["update-ref", "-d", "refs/remotes/origin/main"], Token);
        var unknown = await runner.RunAsync(Run(kit, PullRequestStep(), body: "the body\n"), Token);
        await kit.Harness.RunGitAsync(kit.Main, ["remote", "remove", "origin"], Token);
        var none = await runner.RunAsync(Run(kit, PullRequestStep() with { Base = "main" }, body: "the body\n"), Token);
        await kit.Harness.RunGitAsync(kit.Main, ["checkout", "--quiet", "--detach"], Token);
        var detached = await runner.RunAsync(Run(kit, PullRequestStep() with { Base = "main" }, body: "the body\n"), Token);

        Assert.Equal(
            (false, HarnessExit.ConfigInvalid, "'pr' has a title that is blank once filled, and a pull request takes one: give the step a title, or values that fill it. Nothing was opened."),
            (blank.Passed, blank.Exit, blank.Error));
        Assert.All([elsewhere, itself, unknown, none, detached], refused => Assert.Equal((false, HarnessExit.Refused), (refused.Passed, refused.Exit)));
        Assert.Equal(
            "'pr' opens its pull request on the remote the push step names, 'elsewhere', which is not a remote of this repository (origin): name one of them on the push step, or add it. Nothing was opened.",
            elsewhere.Error);
        Assert.Equal("'pr' would open a pull request from 'work' to itself: the run's work is on a branch of its own, and its base is another. Nothing was opened.", itself.Error);
        Assert.Equal(
            "'pr' cannot tell the branch its pull request goes to: it names no base, and remote 'origin' names no default branch here. Name the base on the step, or read the remote's own with git remote set-head origin --auto. Nothing was opened.",
            unknown.Error);
        Assert.Equal("'pr' opens its pull request on the remote the run's branch goes to, and the repository has no remote: name one on the push step. Nothing was opened.", none.Error);
        Assert.Equal("'pr' opens the pull request of the branch the main checkout is on, and its HEAD is detached: check out the run's branch, then resume. Nothing was opened.", detached.Error);
        Assert.Empty(requests);

        var unread = await Assert.ThrowsAsync<HarnessException>(() => runner.RunAsync(Run(kit, PullRequestStep()), Token));
        Assert.Equal((HarnessExit.InternalError, "'pr' was handed no body read where it started"), (unread.ExitCode, unread.Message));
    }

    /// <summary>
    /// A remote reached over ssh by an alias - one account among several - names the repository on the host ssh's
    /// configuration gives the alias; where ssh cannot say, the forge's tool reads the remotes itself, no --repo given.
    /// </summary>
    [Fact]
    public async Task ARemoteOverSsh_NamesTheRepositoryOnTheHostSshDials()
    {
        using var temp = new TempDirectory();
        var kit = await ForgedAsync(temp);
        await kit.Harness.RunGitAsync(kit.Main, ["remote", "set-url", "origin", "git@work-gh:acme/widgets.git"], Token);
        var requests = new List<ProcessRequest>();
        var found = Exited(0, """[{"number":1,"url":"https://github.com/acme/widgets/pull/1"}]""");

        _ = await PullRequests(kit, requests, Exited(0, "hostname github.com\nuser git\n"), found).RunAsync(Run(kit, PullRequestStep(), body: "b"), Token);
        _ = await PullRequests(kit, requests, Exited(255, stderr: "no ssh here"), found).RunAsync(Run(kit, PullRequestStep(), body: "b"), Token);

        Assert.Equal(
            [
                ["-G", "work-gh"],
                ["pr", "list", "--state", "open", "--json", "number,url", "--limit", "1", "--repo", "github.com/acme/widgets", "--base", "main", "--head", "work"],
                ["-G", "work-gh"],
                ["pr", "list", "--state", "open", "--json", "number,url", "--limit", "1", "--base", "main", "--head", "work"],
            ],
            requests.Select(request => request.Arguments.ToArray()));
    }

    /// <summary>
    /// The forge answering what is no list of pull requests, or opening one without naming its address, fails the step
    /// (20) saying what it answered; one that did not answer in time is not done (21), whether it opened one not known;
    /// and a remote whose address names no repository on a forge leaves the forge's tool to
    /// read the remotes itself, no --repo given.
    /// </summary>
    [Fact]
    public async Task AForgeThatAnswersNothingItCanRead_FailsTheStep_AndAnAddressOfNoForgeNamesNoRepository()
    {
        using var temp = new TempDirectory();
        var kit = await ForgedAsync(temp);
        var requests = new List<ProcessRequest>();

        var unlisted = await Assert.ThrowsAsync<HarnessException>(
            () => PullRequests(kit, requests, null, Exited(0, "{}")).RunAsync(Run(kit, PullRequestStep(), body: "b"), Token));
        var unaddressed = await Assert.ThrowsAsync<HarnessException>(
            () => PullRequests(kit, requests, null, Exited(0, "[]"), Exited(0, "done\n")).RunAsync(Run(kit, PullRequestStep(), body: "b"), Token));
        var late = await Assert.ThrowsAsync<HarnessException>(
            () => PullRequests(kit, requests, null, Exited(0, "[]"), new ProcessResult(-1, string.Empty, string.Empty, TimeSpan.FromMinutes(5), TimedOut: true)).RunAsync(Run(kit, PullRequestStep(), body: "b"), Token));
        await kit.Harness.RunGitAsync(kit.Main, ["remote", "set-url", "origin", temp.Combine("elsewhere.git")], Token);
        requests.Clear();
        _ = await PullRequests(kit, requests, null, Exited(0, """[{"number":1,"url":"https://example.com/pull/1"}]""")).RunAsync(Run(kit, PullRequestStep(), body: "b"), Token);

        Assert.Equal((HarnessExit.CommandFailed, "gh answered no list of pull requests open from 'work': {}"), (unlisted.ExitCode, unlisted.Message));
        Assert.Equal((HarnessExit.CommandFailed, "gh said it opened a pull request from 'work', and named no address of one: done"), (unaddressed.ExitCode, unaddressed.Message));
        Assert.Equal(HarnessExit.Incomplete, late.ExitCode);
        Assert.Contains("did not answer within 5 minutes, so whether a pull request was opened from 'work' to 'main' is not known.", late.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--repo", Assert.Single(requests).Arguments);
    }

    private static WorkflowStep PushStep() => new() { Name = "push", Type = "push" };

    private static WorkflowStep PullRequestStep() => new() { Name = "pr", Type = "pull-request", Title = "the run's work", Body = "body.md" };

    private static PushStepRunner Push(OrchestrationKit kit, IGitPublisher? publisher = null)
        => new(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.GitClient, publisher ?? kit.Harness.GitPublisher);

    /// <summary>
    /// A pull-request step whose forge's tool answers each call with the next of <paramref name="answers"/>, the last again
    /// once they run out, and whose ssh answers each with <paramref name="ssh"/> - not here where null.
    /// </summary>
    private static PullRequestStepRunner PullRequests(OrchestrationKit kit, List<ProcessRequest> requests, ProcessResult? ssh, params ProcessResult[] answers)
    {
        var processes = Substitute.For<IProcessRunner>();
        var next = 0;

        processes.FindExecutable(GhProcess.Executable).Returns(GhProcess.Executable);
        processes.FindExecutable("ssh").Returns(ssh is null ? null : "ssh");
        processes.RunAsync(Arg.Do<ProcessRequest>(requests.Add), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<ProcessRequest>().FileName == "ssh" ? ssh! : answers[Math.Min(next++, answers.Length - 1)]));

        var gh = new GhProcess(processes, GitLocalVariables.Fixed([]), _ => null);

        return new PullRequestStepRunner(
            kit.Harness.ContextLoader,
            kit.Harness.Platform,
            kit.Harness.GitClient,
            new GitPublisher(kit.Harness.GitClient, gh),
            kit.Harness.FileSystem,
            new SshHostNames(processes));
    }

    /// <summary>The workflow, its push step naming <paramref name="remote"/>.</summary>
    private static WorkflowDefinition PushingTo(string remote) => WorkflowFileParser.Parse("workflows/flow/flow.yml", $"""
        name: flow
        steps:
          - name: push
            type: push
            remote: {remote}
          - name: pr
            type: pull-request
            title: the run's work
            body: body.md
            dependsOn: [push]
        """);

    /// <summary>
    /// One run of <paramref name="step"/> on <paramref name="kit"/>'s orchestrator of <paramref name="definition"/>, else
    /// the workflow above, the engine having read <paramref name="body"/> as its body where given; its instance is the
    /// step's name.
    /// </summary>
    private static FlowStepRun Run(OrchestrationKit kit, WorkflowStep step, string? body = null, WorkflowDefinition? definition = null)
        => new(
            step,
            step.Name,
            1,
            FlowValue.Missing,
            null,
            null,
            [],
            new NoEvents(),
            Place: new FlowRunPlace(kit.Main, OrchestrationKit.Orchestrator, null, definition ?? Definition),
            Read: body is null ? null : new Dictionary<string, FlowValue> { ["body"] = FlowValue.Of(body) });

    /// <summary>A run's repository on a branch of its own, <c>work</c>.</summary>
    private static async Task<OrchestrationKit> OnWorkAsync(TempDirectory temp)
    {
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.Harness.RunGitAsync(kit.Main, ["checkout", "--quiet", "-b", "work"], Token);
        return kit;
    }

    /// <summary>A run's repository on <c>work</c>, its <c>origin</c> a bare repository at <paramref name="remote"/>.</summary>
    private static async Task<OrchestrationKit> PushingToAsync(TempDirectory temp, TempDirectory remote)
    {
        var kit = await OnWorkAsync(temp);
        await kit.Harness.RunGitAsync(remote.Path, ["init", "--bare", "--quiet", "."], Token);
        await kit.Harness.RunGitAsync(kit.Main, ["remote", "add", "origin", remote.Path], Token);
        return kit;
    }

    /// <summary>A run's repository on <c>work</c>, its <c>origin</c> a repository on a forge whose default branch, read here, is <c>main</c>.</summary>
    private static async Task<OrchestrationKit> ForgedAsync(TempDirectory temp)
    {
        var kit = await OnWorkAsync(temp);
        await kit.Harness.RunGitAsync(kit.Main, ["remote", "add", "origin", "https://github.com/acme/widgets.git"], Token);
        await kit.Harness.RunGitAsync(kit.Main, ["update-ref", "refs/remotes/origin/main", "HEAD"], Token);
        return kit;
    }

    private static async Task<string> HeadAsync(OrchestrationKit kit) => (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;

    private static ProcessResult Exited(int exitCode, string standardOutput = "", string stderr = "")
        => new(exitCode, standardOutput, stderr, TimeSpan.Zero, TimedOut: false);

    private sealed class NoEvents : IFlowRunEvents
    {
        public void LegPlaced(string leg, bool inACopy)
        {
        }

        public void EveryLegPlaced()
        {
        }

        public void LegCopyMade(string leg)
        {
        }

        public void LegEnded(string leg)
        {
        }

        public void Used(long tokens, double usd)
        {
        }

        public void HandoffTaken()
        {
        }
    }
}
