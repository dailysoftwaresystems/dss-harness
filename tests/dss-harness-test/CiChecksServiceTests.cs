using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.Results;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// A wait for the checks of a commit: it reads every check run and status the forge lists, less those ignored, until one
/// is red - answered at once with where it failed - every one has ended, none appeared in time, or its time is spent, and
/// reads nothing more often than it is told.
/// </summary>
public sealed class CiChecksServiceTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>The step waiting, as the lines the wait says name it.</summary>
    private const string Waiting = "ci-wait";

    private readonly TempDirectory _repository = new();
    private HarnessFactory? _harness;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public void Dispose() => _repository.Dispose();

    /// <summary>
    /// Every check ended green - success, neutral, skipped, a status of success - passes the wait once a second read finds
    /// the same checks; one still running when it first reads is read again a poll later, and nothing between.
    /// </summary>
    [Fact]
    public async Task EveryCheckEndedGreen_PassesTheWait_ReadingAgainAPollLaterUntilTheLastEnds()
    {
        var source = new StubCiJobSource([])
        {
            Readings =
            [
                ([Check("build", "in_progress", null), Check("lint", conclusion: "neutral")], []),
                ([Check("build"), Check("lint", conclusion: "neutral"), Check("docs", conclusion: "skipped")], [new CiCommitStatus("deploy", "success", "https://ci/deploy", "ok")]),
            ],
        };
        var (service, clock) = await ServiceAsync(source);

        var reading = await service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting), Token);

        Assert.Equal(CiChecksFound.Green, reading.Found);
        Assert.Equal(["build", "lint", "docs", "deploy"], reading.Checks.Select(check => check.Name));
        Assert.All(reading.Checks, check => Assert.Equal(CiCheckState.Green, check.State));
        Assert.Equal(3, source.ChecksRead);
        Assert.Equal([TimeSpan.FromSeconds(CiChecksRequest.DefaultPollSeconds), TimeSpan.FromSeconds(CiChecksRequest.DefaultPollSeconds)], clock.Waits);
    }

    /// <summary>
    /// Every check ended on the first read is read again before it passes: a check that appeared since - a workflow's job
    /// the forge listed after a fast status - decides, red here. At the end of the wait every check ended is taken as it
    /// stands, read once.
    /// </summary>
    [Fact]
    public async Task EveryCheckEndedOnItsFirstRead_IsReadAgain_AndOneThatAppearedSinceDecides()
    {
        var late = new StubCiJobSource([])
        {
            Readings =
            [
                ([], [new CiCommitStatus("cla", "success", "", null)]),
                ([], [new CiCommitStatus("cla", "success", "", null), new CiCommitStatus("build", "failure", "", null)]),
            ],
        };
        var atTheEnd = new StubCiJobSource([]) { Readings = [([Check("build")], [])] };

        var red = await (await ServiceAsync(late)).Service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting), Token);
        var taken = await (await ServiceAsync(atTheEnd)).Service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting) { Appear = TimeSpan.Zero, Wait = TimeSpan.Zero }, Token);

        Assert.Equal((CiChecksFound.Red, 2), (red.Found, late.ChecksRead));
        Assert.Equal("build", Assert.Single(red.Red).Check.Name);
        Assert.Equal((CiChecksFound.Green, 1), (taken.Found, atTheEnd.ChecksRead));
    }

    /// <summary>
    /// A wait that reads once - a person's accept - answers what the checks are on that read, each settled on it alone:
    /// none, still running, all ended. A read of it that failed is read again at the next poll, as in any wait, however
    /// short the wait its step says.
    /// </summary>
    [Fact]
    public async Task AWaitThatReadsOnce_AnswersTheChecksAsTheyAre_AReadThatFailedReadAgain()
    {
        var none = new StubCiJobSource([]) { Readings = [([], [])] };
        var running = new StubCiJobSource([]) { Readings = [([Check("build", "in_progress", null)], [])] };
        var ended = new StubCiJobSource([])
        {
            Readings = [([Check("build")], [])],
            FailedReadings = new Dictionary<int, Exception>
            {
                [0] = new HarnessException(HarnessExit.CommandFailed, "HTTP 502"),
                [1] = new HarnessException(HarnessExit.Incomplete, "no answer"),
            },
        };
        var once = new CiChecksRequest(Commit, Waiting) { Once = true };
        var onceOfAShortWait = once with { Wait = TimeSpan.FromMinutes(1), Poll = TimeSpan.FromMinutes(2) };

        var nothing = await (await ServiceAsync(none)).Service.WaitAsync(_repository.Path, once, Token);
        var pending = await (await ServiceAsync(running)).Service.WaitAsync(_repository.Path, once, Token);
        var (service, clock) = await ServiceAsync(ended);
        var green = await service.WaitAsync(_repository.Path, onceOfAShortWait, Token);

        Assert.Equal((CiChecksFound.None, 1), (nothing.Found, none.ChecksRead));
        Assert.Equal((CiChecksFound.Pending, 1), (pending.Found, running.ChecksRead));
        Assert.Equal((CiChecksFound.Green, 3), (green.Found, ended.ChecksRead));
        Assert.Equal([TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2)], clock.Waits);
    }

    /// <summary>
    /// A wait's last poll is cut to its end: its last read is made as its time is spent, never a poll past it - after a
    /// read that failed as after one that answered.
    /// </summary>
    [Fact]
    public async Task AWaitsLastPoll_IsCutToItsEnd()
    {
        var source = new StubCiJobSource([]) { Readings = [([Check("build", "in_progress", null)], [])] };
        var (service, clock) = await ServiceAsync(source);

        var failed = new StubCiJobSource([])
        {
            Readings = [([Check("build", "in_progress", null)], [])],
            FailedReadings = new Dictionary<int, Exception> { [1] = new HarnessException(HarnessExit.CommandFailed, "HTTP 502") },
        };
        var (afterAFailure, failedClock) = await ServiceAsync(failed);
        var request = new CiChecksRequest(Commit, Waiting) { Wait = TimeSpan.FromMinutes(1), Poll = TimeSpan.FromSeconds(45) };

        var reading = await service.WaitAsync(_repository.Path, request, Token);
        var readAgain = await afterAFailure.WaitAsync(_repository.Path, request, Token);

        Assert.Equal((CiChecksFound.Pending, 3), (reading.Found, source.ChecksRead));
        Assert.Equal([TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(15)], clock.Waits);
        Assert.Equal((CiChecksFound.Pending, 3), (readAgain.Found, failed.ChecksRead));
        Assert.Equal([TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(15)], failedClock.Waits);
    }

    /// <summary>
    /// The time it waits for the first check bounds the first alone: a check that appeared and runs past it is waited for,
    /// to its end. And none by the end of a wait shorter than it is none, never checks still running.
    /// </summary>
    [Fact]
    public async Task TheTimeForTheFirstCheck_BoundsTheFirstAlone_AndNoneByAShorterWaitIsNone()
    {
        CiCheckRun running = Check("build", "in_progress", null);
        var slow = new StubCiJobSource([]) { Readings = [([running], []), ([running], []), ([running], []), ([running], []), ([Check("build")], [])] };
        var never = new StubCiJobSource([]) { Readings = [([], [])] };

        var green = await (await ServiceAsync(slow)).Service.WaitAsync(
            _repository.Path,
            new CiChecksRequest(Commit, Waiting) { Appear = TimeSpan.FromSeconds(60), Wait = TimeSpan.FromMinutes(10), Poll = TimeSpan.FromSeconds(30) },
            Token);
        var none = await (await ServiceAsync(never)).Service.WaitAsync(
            _repository.Path,
            new CiChecksRequest(Commit, Waiting) { Appear = TimeSpan.FromSeconds(180), Wait = TimeSpan.FromMinutes(1), Poll = TimeSpan.FromSeconds(30) },
            Token);

        Assert.Equal((CiChecksFound.Green, 5), (green.Found, slow.ChecksRead));
        Assert.Equal((CiChecksFound.None, 3), (none.Found, never.ChecksRead));
    }

    /// <summary>
    /// A read the forge failed - 20 - or that passed its limit - 21 - is said and read again at the next poll; the wait ends
    /// with the failure only where three reads running failed - a read that answered between starts the count again - or
    /// one did as its time was spent; and one refused - 13 - ends it at once.
    /// </summary>
    [Fact]
    public async Task AReadThatFailed_IsReadAgain_UntilThreeRunningOrTheWaitsEnd()
    {
        var mended = new StubCiJobSource([])
        {
            Readings = [([Check("build")], [])],
            FailedReadings = new Dictionary<int, Exception>
            {
                [0] = new HarnessException(HarnessExit.CommandFailed, "HTTP 502"),
                [1] = new HarnessException(HarnessExit.Incomplete, "no answer"),
            },
        };
        var broken = new StubCiJobSource([])
        {
            Readings = [([Check("build")], [])],
            FailedReadings = new Dictionary<int, Exception>
            {
                [0] = new HarnessException(HarnessExit.CommandFailed, "HTTP 502"),
                [1] = new HarnessException(HarnessExit.Incomplete, "no answer"),
                [2] = new HarnessException(HarnessExit.CommandFailed, "HTTP 503"),
            },
        };
        var late = new StubCiJobSource([])
        {
            Readings = [([Check("build", "in_progress", null)], [])],
            FailedReadings = new Dictionary<int, Exception> { [2] = new HarnessException(HarnessExit.CommandFailed, "HTTP 500") },
        };
        var between = new StubCiJobSource([])
        {
            Readings = [([Check("build")], [])],
            FailedReadings = new Dictionary<int, Exception>
            {
                [0] = new HarnessException(HarnessExit.CommandFailed, "HTTP 502"),
                [1] = new HarnessException(HarnessExit.CommandFailed, "HTTP 502"),
                [3] = new HarnessException(HarnessExit.CommandFailed, "HTTP 502"),
            },
        };
        var refused = new StubCiJobSource([])
        {
            Readings = [([Check("build")], [])],
            FailedReadings = new Dictionary<int, Exception> { [0] = new HarnessException(HarnessExit.Refused, "GH_REPO is set") },
        };
        var (service, clock) = await ServiceAsync(mended);

        var green = await service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting), Token);
        var said = _harness!.StandardError.ToString();
        var third = await Assert.ThrowsAsync<HarnessException>(async () => await (await ServiceAsync(broken)).Service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting), Token));
        var spent = await Assert.ThrowsAsync<HarnessException>(async () => await (await ServiceAsync(late)).Service.WaitAsync(
            _repository.Path,
            new CiChecksRequest(Commit, Waiting) { Wait = TimeSpan.FromMinutes(1), Poll = TimeSpan.FromSeconds(30) },
            Token));
        var first = await Assert.ThrowsAsync<HarnessException>(async () => await (await ServiceAsync(refused)).Service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting), Token));
        var counted = await (await ServiceAsync(between)).Service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting), Token);

        Assert.Equal((CiChecksFound.Green, 4), (green.Found, mended.ChecksRead));
        Assert.Equal(
            [
                $"ci-wait: WARN - a read of the checks of {Commit} failed, 1 running of the 3 that end the wait: HTTP 502. They are read again in 30 s.",
                $"ci-wait: WARN - a read of the checks of {Commit} failed, 2 running of the 3 that end the wait: no answer. They are read again in 30 s.",
            ],
            said.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Contains("WARN", StringComparison.Ordinal)));
        Assert.Equal(3, clock.Waits.Count);
        Assert.Equal((HarnessExit.CommandFailed, "HTTP 503", 3), (third.ExitCode, third.Message, broken.ChecksRead));
        Assert.Equal((HarnessExit.CommandFailed, "HTTP 500", 3), (spent.ExitCode, spent.Message, late.ChecksRead));
        Assert.Equal((HarnessExit.Refused, 1), (first.ExitCode, refused.ChecksRead));
        Assert.Equal((CiChecksFound.Green, 5), (counted.Found, between.ChecksRead));
    }

    /// <summary>
    /// A red check whose run or log cannot be read is red all the same: what could not be read is left out and said - the
    /// run's failure once for each of its jobs, read once - and a log that was read is kept.
    /// </summary>
    [Fact]
    public async Task ARedChecksDetailThatCannotBeRead_IsSaidWhy_AndTheCheckIsRedAllTheSame()
    {
        var source = new StubCiJobSource([])
        {
            Readings = [([Check("test (linux)", conclusion: "failure", runId: 7, jobId: 70), Check("test (macos)", conclusion: "failure", runId: 7, jobId: 71)], [])],
            Logs = new Dictionary<long, string> { [70] = "linux log\n" },
            FailedLogs = new Dictionary<long, Exception> { [71] = new HarnessException(HarnessExit.CommandFailed, "log 71 gone") },
        };
        var legs = Substitute.For<ICiLegsService>();
        legs.ClassifyRunAsync(Arg.Any<string>(), 7, Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CiRunReport>(new HarnessException(HarnessExit.CommandFailed, "run 7 not read")));
        var (service, _) = await ServiceAsync(source, legs);

        var reading = await service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting), Token);

        Assert.Equal(CiChecksFound.Red, reading.Found);
        Assert.Equal(
            [
                ("test (linux)", null, null, "linux log\n", "Its run could not be read or classified: run 7 not read"),
                ("test (macos)", null, null, null, "Its run could not be read or classified: run 7 not read log 71 gone"),
            ],
            reading.Red.Select(red => (red.Job, red.Workflow, red.Step, red.Log, red.Unread)));
        await legs.Received(1).ClassifyRunAsync(Arg.Any<string>(), 7, Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A red check's job is the one of its run with the id it points at - never another of its name - and one the forge
    /// lists without its id is found by its name; each with its failed step said.
    /// </summary>
    [Fact]
    public async Task ARedChecksJob_IsTheOneItPointsAt_AndOneListedWithoutItsIdIsFoundByItsName()
    {
        var source = new StubCiJobSource([])
        {
            Readings = [([Check("test (linux)", conclusion: "failure", runId: 7, jobId: 70), Check("lint", conclusion: "failure", runId: 7, jobId: 90)], [])],
            Logs = new Dictionary<long, string> { [70] = "log\n", [90] = "lint log\n" },
        };
        var run = new CiRun(
            7,
            Commit,
            "work",
            "now",
            "failure",
            [
                new CiJob("test (linux)", "failure", [new CiStep("Build", "failure", null, null)]) { Id = 69 },
                new CiJob("test (linux)", "failure", [new CiStep("Test", "failure", null, null)]) { Id = 70 },
                new CiJob("lint", "failure", [new CiStep("Lint", "failure", null, null)]),
            ]);
        var legs = Substitute.For<ICiLegsService>();
        legs.ClassifyRunAsync(Arg.Any<string>(), 7, Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>()).Returns(new CiRunReport(run, "pipeline", false, [], []));
        var (service, _) = await ServiceAsync(source, legs);

        var reading = await service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting), Token);

        Assert.Equal([("pipeline", "Test"), ("pipeline", "Lint")], reading.Red.Select(red => (red.Workflow, red.Step)));
    }

    /// <summary>A wait that is cancelled stops between its reads, and reads nothing more.</summary>
    [Fact]
    public async Task AWaitCancelled_StopsBetweenItsReads()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var source = new StubCiJobSource([])
        {
            Readings = [([Check("build", "in_progress", null)], [])],
            Reading = index =>
            {
                if (index == 2)
                {
                    stop.Cancel();
                }
            },
        };
        var (service, _) = await ServiceAsync(source);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting), stop.Token));

        Assert.Equal(3, source.ChecksRead);
    }

    /// <summary>A wait is timed by the clock's ticks: a clock set a day forward while it waits ends nothing early.</summary>
    [Fact]
    public async Task AClockSetForward_EndsNoWaitEarly()
    {
        var source = new StubCiJobSource([]) { Readings = [([Check("build", "in_progress", null)], [])] };
        var (service, _) = await ServiceAsync(source, clockOf: ticks => new SetForwardAfterItsFirstReading(ticks));

        var reading = await service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting) { Wait = TimeSpan.FromMinutes(1), Poll = TimeSpan.FromSeconds(20) }, Token);

        Assert.Equal((CiChecksFound.Pending, 4), (reading.Found, source.ChecksRead));
    }

    /// <summary>
    /// A red check ends the wait at once, whatever the others come to: of a job of a workflow run, the run is read and its
    /// legs classified once for all its red jobs, and each is answered with its job, workflow, failed step, leg and whole
    /// log; a red status, which is no job, with its name and text alone. A check still running is said pending.
    /// </summary>
    [Fact]
    public async Task ARedCheck_EndsTheWaitAtOnce_EachJobWithWhereItFailed()
    {
        var source = new StubCiJobSource([])
        {
            Readings =
            [
                (
                    [
                        Check("slow", "in_progress", null),
                        Check("test (linux)", conclusion: "failure", runId: 7, jobId: 70),
                        Check("test (macos)", conclusion: "failure", runId: 7, jobId: 71),
                    ],
                    [new CiCommitStatus("license", "error", "https://ci/license", "no license header")]
                ),
            ],
            Logs = new Dictionary<long, string> { [70] = "linux log\n", [71] = "macos log\n" },
        };
        var run = new CiRun(
            7,
            Commit,
            "work",
            "now",
            "failure",
            [
                new CiJob("test (linux)", "failure", [new CiStep("Build", "success", null, null), new CiStep("Test", "failure", null, null)]) { Id = 70 },
                new CiJob("test (macos)", "failure", [new CiStep("Build", "failure", null, null)]) { Id = 71 },
            ]);
        var legs = Substitute.For<ICiLegsService>();
        legs.ClassifyRunAsync(Arg.Any<string>(), 7, Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new CiRunReport(run, "pipeline", true, [], [new CiLegOutcome("linux", "test (linux)", false, ["failed"], true, false, [], 50, 45, "job name")]));
        var (service, clock) = await ServiceAsync(source, legs);

        var reading = await service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting) { ReadLimit = TimeSpan.FromMinutes(9) }, Token);

        Assert.Equal(CiChecksFound.Red, reading.Found);
        Assert.Empty(clock.Waits);
        Assert.Equal(["slow"], reading.Pending.Select(check => check.Name));
        Assert.Equal(
            [
                ("test (linux)", "test (linux)", "pipeline", "Test", "linux", true, "linux log\n"),
                ("test (macos)", "test (macos)", "pipeline", "Build", null, false, "macos log\n"),
                ("license", null, null, null, null, false, null),
            ],
            reading.Red.Select(red => (red.Check.Name, red.Job, red.Workflow, red.Step, red.Leg, red.Overran, red.Log)));
        Assert.Equal("no license header", reading.Red[2].Check.Description);
        await legs.Received(1).ClassifyRunAsync(_repository.Path, 7, TimeSpan.FromMinutes(9), Arg.Any<CancellationToken>());
        Assert.All(source.ReadLimits, limit => Assert.Equal(TimeSpan.FromMinutes(9), limit));
    }

    /// <summary>No check appearing within the time it waits for the first ends the wait with none, read at each poll till then.</summary>
    [Fact]
    public async Task NoCheckAppearingInTime_EndsTheWaitWithNone()
    {
        var source = new StubCiJobSource([]) { Readings = [([], [])] };
        var (service, clock) = await ServiceAsync(source);

        var reading = await service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting) { Appear = TimeSpan.FromSeconds(60), Poll = TimeSpan.FromSeconds(30) }, Token);

        Assert.Equal(CiChecksFound.None, reading.Found);
        Assert.Empty(reading.Checks);
        Assert.Equal(3, source.ChecksRead);
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)], clock.Waits);
    }

    /// <summary>Checks still running when the time to wait is spent end it pending, naming those still running; a status pending is one.</summary>
    [Fact]
    public async Task ChecksStillRunningAtTheirTime_EndTheWaitPending()
    {
        var source = new StubCiJobSource([]) { Readings = [([Check("build")], [new CiCommitStatus("deploy", "pending", "", null)])] };
        var (service, _) = await ServiceAsync(source);

        var reading = await service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting) { Wait = TimeSpan.FromMinutes(1), Poll = TimeSpan.FromSeconds(20) }, Token);

        Assert.Equal(CiChecksFound.Pending, reading.Found);
        Assert.Equal(["deploy"], reading.Pending.Select(check => check.Name));
        Assert.Null(reading.Pending[0].Conclusion);
        Assert.Equal(4, source.ChecksRead);
    }

    /// <summary>Every check ended, none red, and one cancelled or timed out: the wait ends unsettled, naming it - never green, never red.</summary>
    [Theory]
    [InlineData("cancelled")]
    [InlineData("timed_out")]
    [InlineData("action_required")]
    public async Task ACheckEndedNeitherGreenNorRed_EndsTheWaitUnsettled(string conclusion)
    {
        var source = new StubCiJobSource([]) { Readings = [([Check("build"), Check("e2e", conclusion: conclusion)], [])] };
        var (service, _) = await ServiceAsync(source);

        var reading = await service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting), Token);

        Assert.Equal(CiChecksFound.Unsettled, reading.Found);
        Assert.Equal(["e2e"], reading.Unsettled.Select(check => check.Name));
    }

    /// <summary>A check ignored - by its name written out, or with * standing for any run of characters - decides nothing and is not said.</summary>
    [Fact]
    public async Task AnIgnoredCheck_DecidesNothing_AndIsNotSaid()
    {
        var source = new StubCiJobSource([])
        {
            Readings = [([Check("build"), Check("flaky / e2e (linux)", conclusion: "failure", runId: 3, jobId: 30), Check("lint", "queued", null)], [new CiCommitStatus("Lint", "error", "", null)])],
        };
        var (service, _) = await ServiceAsync(source);

        var reading = await service.WaitAsync(_repository.Path, new CiChecksRequest(Commit, Waiting) { Ignore = ["flaky*", "lint", "Lint"] }, Token);

        Assert.Equal(CiChecksFound.Green, reading.Found);
        Assert.Equal(["build"], reading.Checks.Select(check => check.Name));
    }

    /// <summary>Which names an ignore leaves out: equal to one written out, ordinally, or matching one with *, which stands for any run of characters, none among them.</summary>
    [Theory]
    [InlineData("lint", "lint", true)]
    [InlineData("lint", "Lint", false)]
    [InlineData("lint", "lint (x)", false)]
    [InlineData("e2e*", "e2e", true)]
    [InlineData("e2e*", "e2e / chrome (1/2)", true)]
    [InlineData("*(macos)", "test (macos)", true)]
    [InlineData("*(macos)", "test (macos) again", false)]
    [InlineData("a.c*", "abc", false)]
    public void AnIgnore_LeavesOutTheNamesItWritesOrMatches(string pattern, string name, bool ignored)
        => Assert.Equal(ignored, new CiChecksRequest(Commit, Waiting) { Ignore = [pattern] }.Ignores(name));

    /// <summary>What a check came to, by its conclusion or its status's state, said in one place.</summary>
    [Theory]
    [InlineData("success", CiCheckState.Green)]
    [InlineData("neutral", CiCheckState.Green)]
    [InlineData("skipped", CiCheckState.Green)]
    [InlineData("failure", CiCheckState.Red)]
    [InlineData("cancelled", CiCheckState.Unsettled)]
    [InlineData("stale", CiCheckState.Unsettled)]
    [InlineData("startup_failure", CiCheckState.Unsettled)]
    [InlineData(null, CiCheckState.Unsettled)]
    public void ACheckRunsConclusion_SaysWhatItCameTo(string? conclusion, CiCheckState state)
    {
        Assert.Equal(state, CiConclusions.Of(conclusion));
        Assert.Equal(CiCheckState.Pending, new CiCheckRun("x", "in_progress", conclusion, "", null, null).State);
    }

    /// <summary>What a commit status came to, by its state.</summary>
    [Theory]
    [InlineData("success", CiCheckState.Green)]
    [InlineData("failure", CiCheckState.Red)]
    [InlineData("error", CiCheckState.Red)]
    [InlineData("pending", CiCheckState.Pending)]
    [InlineData("unknown", CiCheckState.Unsettled)]
    public void AStatusesState_SaysWhatItCameTo(string state, CiCheckState reading)
        => Assert.Equal(reading, new CiCommitStatus("x", state, "", null).Reading);

    private static CiCheckRun Check(string name, string status = CiCheckRun.Completed, string? conclusion = "success", long? runId = null, long? jobId = null)
        => new(name, status, conclusion, $"https://ci/{name}", runId, jobId);

    private async Task<(CiChecksService Service, JumpingClock Clock)> ServiceAsync(StubCiJobSource source, ICiLegsService? legs = null, Func<JumpingClock, TimeProvider>? clockOf = null)
    {
        var clock = new JumpingClock();

        // One repository for the test, however many services it makes.
        if (_harness is null)
        {
            _harness = new HarnessFactory();
            await _harness.InitializeHarnessAsync(_repository.Path, Token, new HarnessConfig());
        }

        return (new CiChecksService(_harness.ContextLoader, source, legs ?? Substitute.For<ICiLegsService>(), _harness.Output, clockOf?.Invoke(clock) ?? clock), clock);
    }

    /// <summary>A clock whose ticks are <paramref name="ticks"/>', and whose time of day is set a day forward once it has been read once.</summary>
    private sealed class SetForwardAfterItsFirstReading(JumpingClock ticks) : TimeProvider
    {
        private int _read;

        public override long TimestampFrequency => ticks.TimestampFrequency;

        public override DateTimeOffset GetUtcNow()
            => ticks.GetUtcNow() + (Interlocked.Increment(ref _read) > 1 ? TimeSpan.FromDays(1) : TimeSpan.Zero);

        public override long GetTimestamp() => ticks.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => ticks.CreateTimer(callback, state, dueTime, period);
    }
}
