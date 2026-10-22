using DssHarness.Core.Ci;
using DssHarness.Core.Git;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// The forge's command line is started one way, whoever asks - so it cannot ask, ended at its limit, and refused where
/// it is not there or failed - and the reads of CI go through it: each listing whole, page by page, and each check of a
/// commit said as the job of a workflow run it is.
/// </summary>
public sealed class GhCiJobSourceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Every call is started so that it cannot ask - gh, a credential manager, git - without any variable git reads a
    /// repository from, and with its caller's limit, five minutes where it names none.
    /// </summary>
    [Fact]
    public async Task EveryCall_CannotAsk_AndIsGivenItsLimit()
    {
        var requests = new List<ProcessRequest>();
        var gh = Gh(requests, Exited(0, "ok"));

        _ = await gh.RunAsync("/repo", ["api", "x"], limit: null, "so nothing was read", Token);
        _ = await gh.RunAsync("/repo", ["api", "y"], TimeSpan.FromMinutes(12), "so nothing was read", Token);

        Assert.All(requests, request =>
        {
            Assert.Equal("1", request.Environment["GH_PROMPT_DISABLED"]);
            Assert.Equal("never", request.Environment["GCM_INTERACTIVE"]);
            Assert.Equal("0", request.Environment["GIT_TERMINAL_PROMPT"]);
            Assert.True(request.Environment.TryGetValue("GIT_DIR", out var dir) && dir is null);
            Assert.Equal("/repo", request.WorkingDirectory);
        });
        Assert.Equal([GhProcess.DefaultLimit, TimeSpan.FromMinutes(12)], requests.Select(request => request.Timeout));
    }

    /// <summary>
    /// A call that passed its limit is not done (21) and may be asked again; one that failed is 20, with what it said; and
    /// gh not being there is 14 - each saying what it left undone.
    /// </summary>
    [Fact]
    public async Task ACallPastItsLimit_IsIncomplete_AFailedOne20_AndNoGh14()
    {
        var late = await Assert.ThrowsAsync<HarnessException>(
            () => Gh([], new ProcessResult(-1, string.Empty, string.Empty, TimeSpan.FromMinutes(5), TimedOut: true)).RunAsync("/repo", ["api", "x"], null, "so CI was NOT read", Token));
        var failed = await Assert.ThrowsAsync<HarnessException>(
            () => Gh([], Exited(1, stderr: "HTTP 404: Not Found\n")).RunAsync("/repo", ["api", "x"], null, "so CI was NOT read", Token));

        var runner = Substitute.For<IProcessRunner>();
        runner.FindExecutable(GhProcess.Executable).Returns((string?)null);
        var missing = await Assert.ThrowsAsync<HarnessException>(
            () => new GhProcess(runner, GitLocalVariables.Fixed([]), NoVariables).RunAsync("/repo", ["api", "x"], null, "so CI was NOT read", Token));

        Assert.Equal(
            (HarnessExit.Incomplete, "gh api x did not answer within 5 minutes, so CI was NOT read. Asked again, it may answer: a forge that is slow or a network that dropped says nothing of the code."),
            (late.ExitCode, late.Message));
        Assert.Equal((HarnessExit.CommandFailed, "gh api x failed, so CI was NOT read: HTTP 404: Not Found"), (failed.ExitCode, failed.Message));
        Assert.Equal(HarnessExit.ToolMissing, missing.ExitCode);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, Token);
    }

    /// <summary>
    /// GH_REPO naming a repository is refused (13) before gh is looked for or started: the call answers for the tree it
    /// was pointed at, and would answer for that one instead.
    /// </summary>
    [Fact]
    public async Task ARepositoryNamedInGhRepo_IsRefused_AndNothingStarts()
    {
        var runner = Substitute.For<IProcessRunner>();
        var gh = new GhProcess(runner, GitLocalVariables.Fixed([]), name => name == GhProcess.RepositoryOverrideVariable ? "other/repo" : null);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => gh.RunAsync("/repo", ["api", "x"], null, "so CI was NOT read", Token));

        Assert.Equal(
            (HarnessExit.Refused, "GH_REPO is set (other/repo), so gh would answer for THAT repository, not the tree it was pointed at, so CI was NOT read. Unset it for this call."),
            (refused.ExitCode, refused.Message));
        _ = runner.DidNotReceiveWithAnyArgs().FindExecutable(default!);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, Token);
    }

    /// <summary>
    /// A commit's check runs are read page by page until the count the forge gives is reached, each a job of a workflow run
    /// where the address it points at names the run and the job, and a check of its own otherwise.
    /// </summary>
    [Fact]
    public async Task ACommitsCheckRuns_AreReadWhole_EachTheJobItIs()
    {
        var requests = new List<ProcessRequest>();
        var first = string.Join(",", Enumerable.Range(1, 100).Select(index =>
            $$"""{"name":"test ({{index}})","status":"completed","conclusion":"success","html_url":"https://github.com/o/r/runs/{{index}}","details_url":"https://github.com/o/r/actions/runs/7/job/{{index}}"}"""));
        var gh = Gh(
            requests,
            Exited(0, $$"""{"total_count":101,"check_runs":[{{first}}]}"""),
            Exited(0, """{"total_count":101,"check_runs":[{"name":"license","status":"queued","conclusion":null,"html_url":"https://checks/license","details_url":"https://elsewhere/license"}]}"""));

        var checks = await new GhCiJobSource(gh).ListCheckRunsAsync("/repo", "abc123", TimeSpan.FromMinutes(3), Token);

        Assert.Equal(101, checks.Count);
        Assert.Equal(new CiCheckRun("test (1)", "completed", "success", "https://github.com/o/r/runs/1", 7, 1), checks[0]);
        Assert.Equal(new CiCheckRun("license", "queued", null, "https://checks/license", null, null), checks[^1]);
        Assert.Equal(
            [
                ["api", "repos/:owner/:repo/commits/abc123/check-runs?per_page=100&page=1"],
                ["api", "repos/:owner/:repo/commits/abc123/check-runs?per_page=100&page=2"],
            ],
            requests.Select(request => request.Arguments.ToArray()));
        Assert.All(requests, request => Assert.Equal(TimeSpan.FromMinutes(3), request.Timeout));
    }

    /// <summary>
    /// Fewer entries than the forge says it has are never read as all of them - of check runs, statuses or a run's jobs:
    /// the read fails, saying so.
    /// </summary>
    [Theory]
    [InlineData("check runs", "The check runs of abc123 report 3 check run(s) and only 1 came back, so some were never read. This is not a pass.")]
    [InlineData("statuses", "The statuses of abc123 report 3 status(es) and only 1 came back, so some were never read. This is not a pass.")]
    [InlineData("jobs", "The jobs of run 7 report 2 job(s) and only 1 came back, so some were never read. This is not a pass.")]
    public async Task FewerEntriesThanTheForgeCounts_FailTheRead(string listing, string message)
    {
        var source = new GhCiJobSource(Gh(
            [],
            listing switch
            {
                "check runs" => new[] { Exited(0, """{"total_count":3,"check_runs":[{"name":"a","status":"completed","conclusion":"success"}]}""") },
                "statuses" => new[] { Exited(0, """{"state":"success","total_count":3,"statuses":[{"context":"a","state":"success"}]}""") },
                _ => new[] { Exited(0, """{"head_sha":"abc","name":"pipeline"}"""), Exited(0, """{"total_count":2,"jobs":[{"id":1,"name":"a"}]}""") },
            }));

        var short_ = await Assert.ThrowsAsync<HarnessException>(() => listing switch
        {
            "check runs" => source.ListCheckRunsAsync("/repo", "abc123", null, Token),
            "statuses" => source.ListStatusesAsync("/repo", "abc123", null, Token),
            _ => source.ReadRunAsync("/repo", 7, null, Token),
        });

        Assert.Equal((HarnessExit.CommandFailed, message), (short_.ExitCode, short_.Message));
    }

    /// <summary>
    /// A page with no list of its entries, or an answer that is not JSON, is never read as no entries: the read fails,
    /// saying CI was not read; and a log that could not be read says so of the log.
    /// </summary>
    [Fact]
    public async Task AnAnswerNotAListing_FailsTheRead_AndALogNotReadSaysSo()
    {
        var noRuns = await Assert.ThrowsAsync<HarnessException>(() => new GhCiJobSource(Gh([], Exited(0, """{"total_count":0}"""))).ListCheckRunsAsync("/repo", "abc123", null, Token));
        var noStatuses = await Assert.ThrowsAsync<HarnessException>(() => new GhCiJobSource(Gh([], Exited(0, """{"message":"Not Found"}"""))).ListStatusesAsync("/repo", "abc123", null, Token));
        var notJson = await Assert.ThrowsAsync<HarnessException>(() => new GhCiJobSource(Gh([], Exited(0, "<html>busy</html>"))).ListCheckRunsAsync("/repo", "abc123", null, Token));
        var noLog = await Assert.ThrowsAsync<HarnessException>(() => new GhCiJobSource(Gh([], Exited(1, stderr: "HTTP 410: Gone\n"))).ReadJobLogAsync("/repo", 456, null, Token));

        Assert.Equal((HarnessExit.CommandFailed, "gh answered no 'check_runs' list for the check runs of abc123, so CI was NOT read (this is not a pass)."), (noRuns.ExitCode, noRuns.Message));
        Assert.Equal((HarnessExit.CommandFailed, "gh answered no 'statuses' list for the statuses of abc123, so CI was NOT read (this is not a pass)."), (noStatuses.ExitCode, noStatuses.Message));
        Assert.Equal(HarnessExit.CommandFailed, notJson.ExitCode);
        Assert.StartsWith("gh answered something that is not JSON for the check runs of abc123, so CI was NOT read (this is not a pass): ", notJson.Message, StringComparison.Ordinal);
        Assert.Equal((HarnessExit.CommandFailed, "gh api repos/:owner/:repo/actions/jobs/456/logs failed, so the log of job 456 was NOT read: HTTP 410: Gone"), (noLog.ExitCode, noLog.Message));
    }

    /// <summary>A commit's statuses are read as the forge combines them, the newest of each context; a job's log as the forge keeps it.</summary>
    [Fact]
    public async Task ACommitsStatuses_AndAJobsLog_AreReadAsTheForgeKeepsThem()
    {
        var requests = new List<ProcessRequest>();
        var gh = Gh(
            requests,
            Exited(0, """{"state":"failure","total_count":2,"statuses":[{"context":"deploy","state":"error","target_url":"https://ci/d","description":"it broke"},{"context":"docs","state":"success","target_url":null,"description":null}]}"""),
            Exited(0, "2026-10-22T10:00:00Z line one\n2026-10-22T10:00:01Z line two\n"));
        var source = new GhCiJobSource(gh);

        var statuses = await source.ListStatusesAsync("/repo", "abc123", null, Token);
        var log = await source.ReadJobLogAsync("/repo", 456, null, Token);

        Assert.Equal([new CiCommitStatus("deploy", "error", "https://ci/d", "it broke"), new CiCommitStatus("docs", "success", string.Empty, null)], statuses);
        Assert.Equal("2026-10-22T10:00:00Z line one\n2026-10-22T10:00:01Z line two\n", log);
        Assert.Equal(
            [
                ["api", "repos/:owner/:repo/commits/abc123/status?per_page=100&page=1"],
                ["api", "repos/:owner/:repo/actions/jobs/456/logs"],
            ],
            requests.Select(request => request.Arguments.ToArray()));
    }

    /// <summary>A run is read with its workflow's name and file, and each job with its id, which a check of it points at.</summary>
    [Fact]
    public async Task ARun_IsReadWithItsWorkflow_AndEachJobWithItsId()
    {
        var gh = Gh(
            [],
            Exited(0, """{"head_sha":"abc","head_branch":"work","created_at":"now","conclusion":"failure","name":"pipeline","path":".github/workflows/pipeline.yml"}"""),
            Exited(0, """{"total_count":1,"jobs":[{"id":70,"name":"test (linux)","conclusion":"failure","steps":[{"name":"Test","conclusion":"failure"}]}]}"""));

        var run = await new GhCiJobSource(gh).ReadRunAsync("/repo", 7, null, Token);

        Assert.Equal(("pipeline", ".github/workflows/pipeline.yml"), (run.Workflow, run.WorkflowPath));
        Assert.Equal(70, Assert.Single(run.Jobs).Id);
    }

    /// <summary>An environment naming no variable, so no repository is named for gh whatever this machine's names.</summary>
    private static string? NoVariables(string name) => null;

    /// <summary>The forge's tool, answering each call with the next of <paramref name="answers"/>, the last again once they run out.</summary>
    private static GhProcess Gh(List<ProcessRequest> requests, params ProcessResult[] answers)
    {
        var runner = Substitute.For<IProcessRunner>();
        var next = 0;

        runner.FindExecutable(GhProcess.Executable).Returns(GhProcess.Executable);
        runner.RunAsync(Arg.Do<ProcessRequest>(requests.Add), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(answers[Math.Min(next++, answers.Length - 1)]));

        return new GhProcess(runner, GitLocalVariables.Fixed(["GIT_DIR"]), NoVariables);
    }

    private static ProcessResult Exited(int exitCode, string standardOutput = "", string stderr = "")
        => new(exitCode, standardOutput, stderr, TimeSpan.Zero, TimedOut: false);
}
