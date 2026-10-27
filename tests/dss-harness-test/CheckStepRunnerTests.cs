using System.Text.Json.Nodes;
using DssHarness.Core.Anchors;
using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Schemas;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// The checks that run no legs as steps: anchor checks held to the rows or files a step names, CI read and waited for,
/// each answering what its type's schema says, red where its check found red with the code its check gives, and a wait
/// that found nothing to end on asking.
/// </summary>
public sealed class CheckStepRunnerTests : IDisposable
{
    private readonly List<TempDirectory> _made = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly WorkflowDefinition Flow = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
        name: flow
        steps:
          - name: orient
            type: orient
        """);

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var made in _made)
        {
            made.Dispose();
        }
    }

    /// <summary>
    /// An anchor balance measures from its base in its tree and answers what failed it - each anchor counted as created
    /// where it counts more opened than closed, and each problem that stops a registry being trusted - as lines the check
    /// prints, red with the check's code; held to rows, only their anchors and the problems at their lines count, with
    /// every problem of a registry as a whole; rows an expression yielded empty name nothing, and nothing fails.
    /// </summary>
    [Fact]
    public async Task AnAnchorBalance_AnswersWhatFailedIt_HeldToItsRows()
    {
        var (harness, repository) = await RepositoryAsync();
        var balance = Substitute.For<IAnchorBalanceService>();
        balance.CheckAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Balance());
        var runner = new AnchorBalanceStepRunner(harness.ContextLoader, harness.Platform, balance);
        var step = new WorkflowStep { Name = "balance", Type = "anchor-balance", Base = "origin/main" };

        var whole = await runner.RunAsync(Run(step, repository.Path), Token);
        var heldToTheirs = await runner.RunAsync(Run(step with { Rows = new FlowList(["A-ONE", "A-TWO"], null) }, repository.Path), Token);
        var heldToTheBroken = await runner.RunAsync(Run(step with { Rows = new FlowList([], "fold.rows") }, repository.Path, read: new Dictionary<string, FlowValue> { ["rows"] = FlowValue.Of(new JsonArray("A-FOUR")) }), Token);
        var heldToNothing = await runner.RunAsync(Run(step with { Rows = new FlowList([], "fold.rows") }, repository.Path, read: new Dictionary<string, FlowValue> { ["rows"] = FlowValue.Of(new JsonArray()) }), Token);

        await balance.Received(4).CheckAsync(repository.Path, "origin/main", Arg.Any<CancellationToken>());
        Assert.Equal((true, true, AnchorExit.Findings), (whole.Passed, whole.Red, whole.Exit));
        Assert.Equal(
            (false, "+ A-TWO   does x | + A-THREE   does y | anchors/pending.md:7   row A-FOUR cannot be read | anchors/pending.md:0   the registry has no header"),
            Findings(whole));
        Assert.Equal((false, "anchors/pending.md:0   the registry has no header"), Findings(heldToTheirs));
        Assert.Equal((false, "anchors/pending.md:7   row A-FOUR cannot be read | anchors/pending.md:0   the registry has no header"), Findings(heldToTheBroken));
        Assert.True(heldToTheirs.Red);

        balance.CheckAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Balance(registryWide: AnchorFindingSeverity.Note));
        var passed = await runner.RunAsync(Run(step with { Rows = new FlowList([], "fold.rows") }, repository.Path, read: new Dictionary<string, FlowValue> { ["rows"] = FlowValue.Of(new JsonArray()) }), Token);

        Assert.Equal((true, false, HarnessExit.Success, null), (passed.Passed, passed.Red, passed.Exit, passed.Error));
        Assert.Equal((true, ""), Findings(passed));
        Assert.False(heldToNothing.Output!["passed"]!.GetValue<bool>());
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("anchor-balance")!.Output!)!, whole.Output));
    }

    /// <summary>
    /// An anchor citations check judges its subject - the files on disk where it names none - and answers each citation
    /// that resolves to no row as the check prints it; held to paths, only those in the files named count, each named as a
    /// citation names its file whatever its separators.
    /// </summary>
    [Fact]
    public async Task AnAnchorCitationsCheck_JudgesItsSubject_HeldToItsPaths()
    {
        var (harness, repository) = await RepositoryAsync();
        var citations = Substitute.For<IAnchorCitationService>();
        var report = new AnchorCitationReport(
            AnchorCitationSubject.CurrentTree,
            "the working tree",
            ["src"],
            3,
            5,
            9,
            [new AnchorCitation("A-ONE", "src/a.cs", 4), new AnchorCitation("A-TW", "docs/b.md", 2) { Cut = true, Written = "A-TW-" }]);
        citations.CheckAsync(Arg.Any<string>(), Arg.Any<AnchorCitationSubject>(), Arg.Any<CancellationToken>()).Returns(report);
        var runner = new AnchorCitationsStepRunner(harness.ContextLoader, harness.Platform, citations);
        var step = new WorkflowStep { Name = "cites", Type = "anchor-citations" };

        var whole = await runner.RunAsync(Run(step, repository.Path), Token);
        var held = await runner.RunAsync(Run(step with { Subject = "currentCommit", AnchorPaths = new FlowList(["./docs\\b.md"], null) }, repository.Path), Token);
        var elsewhere = await runner.RunAsync(Run(step with { AnchorPaths = new FlowList(["src/other.cs"], null) }, repository.Path), Token);

        await citations.Received(2).CheckAsync(repository.Path, AnchorCitationSubject.CurrentTree, Arg.Any<CancellationToken>());
        await citations.Received(1).CheckAsync(repository.Path, AnchorCitationSubject.CurrentCommit, Arg.Any<CancellationToken>());
        Assert.Equal("""{"passed":false,"findings":["src/a.cs:4: A-ONE","docs/b.md:2: A-TW- (cut at the end of the line)"]}""", whole.Output!.ToJsonString());
        Assert.Equal((true, AnchorExit.Findings, "2 citation(s) of the working tree resolve to no row"), (whole.Red, whole.Exit, whole.Error));
        Assert.Equal("""{"passed":false,"findings":["docs/b.md:2: A-TW- (cut at the end of the line)"]}""", held.Output!.ToJsonString());
        Assert.Equal((false, HarnessExit.Success), (elsewhere.Red, elsewhere.Exit));
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("anchor-citations")!.Output!)!, whole.Output));
    }

    /// <summary>
    /// A CI read reads its branch's newest runs as check-ci-legs does, each read given the step's time limit where it says
    /// one - gh's own five minutes where it does not - and answers each leg as check-ci-legs --json gives it: red where a
    /// leg is red (1) or the matrix never ran (2), passed where every leg is green.
    /// </summary>
    [Fact]
    public async Task ACiRead_AnswersItsLegs_RedWhereOneIsOrNoneRan()
    {
        var (harness, repository) = await RepositoryAsync();
        var legs = Substitute.For<ICiLegsService>();
        var run = new CiRun(1, "abc", "work", "now", "failure", []);
        var red = new CiLegOutcome("linux", "unit (linux)", false, ["the Test step failed"], false, false, [], 10, 60, "job name");
        var green = red with { Leg = "mac", Job = "unit (mac)", Success = true, Errors = [] };
        legs.CheckAsync(Arg.Any<string>(), Arg.Any<CiLegsRequest>(), Arg.Any<CancellationToken>()).Returns(
            new CiLegsReport("work", [new CiRunReport(run, "Pipeline", true, [], [red, green])]),
            new CiLegsReport("work", [new CiRunReport(run, "Pipeline", false, ["gate=failure"], [])]),
            new CiLegsReport("work", [new CiRunReport(run, "Pipeline", true, [], [green])]));
        var runner = new CiReadStepRunner(harness.ContextLoader, harness.Platform, legs);
        var step = new WorkflowStep { Name = "ci", Type = "ci-read", Branch = "release", Limit = 3, TimeoutMinutes = 7 };

        var redLeg = await runner.RunAsync(Run(step, repository.Path), Token);
        var noMatrix = await runner.RunAsync(Run(step, repository.Path), Token);
        var allGreen = await runner.RunAsync(Run(step with { TimeoutMinutes = null }, repository.Path), Token);

        await legs.Received(2).CheckAsync(
            repository.Path,
            Arg.Is<CiLegsRequest>(request => request.Branch == "release" && request.Limit == 3 && request.Runs.Count == 0 && request.ReadTimeLimit == TimeSpan.FromMinutes(7)),
            Arg.Any<CancellationToken>());
        await legs.Received(1).CheckAsync(repository.Path, Arg.Is<CiLegsRequest>(request => request.ReadTimeLimit == null), Arg.Any<CancellationToken>());
        Assert.Equal((true, CiExit.LegRed), (redLeg.Red, redLeg.Exit));
        Assert.Equal(["linux"], redLeg.Output!["red"]!.AsArray().Select(leg => leg!["leg"]!.GetValue<string>()));
        Assert.Equal(["linux", "mac"], redLeg.Output!["legs"]!.AsArray().Select(leg => leg!["leg"]!.GetValue<string>()));
        Assert.Equal((true, CiExit.MatrixDidNotRun, false), (noMatrix.Red, noMatrix.Exit, noMatrix.Output!["matrixRan"]!.GetValue<bool>()));
        Assert.Equal((false, HarnessExit.Success, true), (allGreen.Red, allGreen.Exit, allGreen.Output!["passed"]!.GetValue<bool>()));
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("ci-read")!.Output!)!, redLeg.Output));
    }

    /// <summary>
    /// A wait for checks waits for its commit - the one the run last pushed where it names none - with the step's keys,
    /// each read given the step's time limit - gh's own five minutes where it says none; one that names no commit in a run
    /// that pushed none is refused (13) and reads nothing. None found, some still running, or one unsettled asks; all green passes; and accepted, it reads once more,
    /// waiting for nothing, and is done with the checks as they are - none, some still running, all green by then - its
    /// passed true only where they are green.
    /// </summary>
    [Fact]
    public async Task ACiWait_WaitsForItsCommit_AndAsksWhereItCannotEnd()
    {
        var (harness, repository) = await RepositoryAsync();
        var checks = Substitute.For<ICiChecksService>();
        var green = new CiCheck("build", CiCheckState.Green, "success", "https://ci/build", null, null, null);
        var running = new CiCheck("deploy", CiCheckState.Pending, null, "https://ci/deploy", null, null, null);
        var cancelled = new CiCheck("e2e", CiCheckState.Unsettled, "cancelled", "https://ci/e2e", null, null, null);
        var failed = new CiCheck("license", CiCheckState.Red, "failure", "https://ci/license", null, null, "no header");
        checks.WaitAsync(Arg.Any<string>(), Arg.Any<CiChecksRequest>(), Arg.Any<CancellationToken>()).Returns(
            new CiChecksReading("pushed", CiChecksFound.Green, [green], []),
            new CiChecksReading("named", CiChecksFound.None, [], []),
            new CiChecksReading("named", CiChecksFound.Pending, [green, running], []),
            new CiChecksReading("named", CiChecksFound.Unsettled, [green, cancelled], []),
            new CiChecksReading("named", CiChecksFound.None, [], []),
            new CiChecksReading("named", CiChecksFound.Pending, [green, running], []),
            new CiChecksReading("named", CiChecksFound.Green, [green], []),
            new CiChecksReading("named", CiChecksFound.Red, [failed], [new CiRedCheck(failed, null, null, null, null, false, null, null)]));
        var runner = Runner(harness, checks);
        var step = new WorkflowStep { Name = "wait", Type = "ci-wait", AppearSeconds = 60, WaitMinutes = 20, PollSeconds = 15, Ignore = ["lint"], TimeoutMinutes = 30 };

        var unnamed = await runner.RunAsync(Run(step, repository.Path), Token);
        var passed = await runner.RunAsync(Run(step, repository.Path, read: new Dictionary<string, FlowValue> { ["commit"] = FlowValue.Of("pushed") }), Token);
        var none = await runner.RunAsync(Run(step with { Commit = "named" }, repository.Path), Token);
        var pending = await runner.RunAsync(Run(step with { Commit = "named" }, repository.Path), Token);
        var unsettled = await runner.RunAsync(Run(step with { Commit = "named" }, repository.Path), Token);
        FlowAnswerGiven[] accept = [new FlowAnswerGiven("q1", "accept")];
        var acceptedNone = await runner.RunAsync(Run(step with { Commit = "named" }, repository.Path, answers: accept), Token);
        var acceptedPending = await runner.RunAsync(Run(step with { Commit = "named" }, repository.Path, answers: accept), Token);
        var acceptedGreen = await runner.RunAsync(Run(step with { Commit = "named" }, repository.Path, answers: accept), Token);
        var acceptedRed = await runner.RunAsync(Run(step with { Commit = "named" }, repository.Path, answers: accept), Token);

        Assert.Equal((false, HarnessExit.Refused), (unnamed.Passed, unnamed.Exit));
        Assert.Equal("'wait' names no commit, and the run has pushed none whose checks it could wait for. Nothing was read.", unnamed.Error);
        await checks.Received(1).WaitAsync(
            repository.Path,
            Arg.Is<CiChecksRequest>(request => request.Commit == "pushed"
                && request.Appear == TimeSpan.FromSeconds(60)
                && request.Wait == TimeSpan.FromMinutes(20)
                && request.Poll == TimeSpan.FromSeconds(15)
                && request.Ignore.SequenceEqual(new[] { "lint" })
                && request.ReadTimeLimit == TimeSpan.FromMinutes(30)
                && !request.Once),
            Arg.Any<CancellationToken>());
        Assert.Equal(
            """{"passed":true,"commit":"pushed","checks":[{"name":"build","state":"green","conclusion":"success","url":"https://ci/build"}],"red":[],"pending":[],"unsettled":[]}""",
            passed.Output!.ToJsonString());
        Assert.Equal(("none", "no check appeared on named within 60 s"), (none.Checks, none.ChecksDetail));
        var noneYet = Substitute.For<ICiChecksService>();
        noneYet.WaitAsync(Arg.Any<string>(), Arg.Any<CiChecksRequest>(), Arg.Any<CancellationToken>()).Returns(new CiChecksReading("named", CiChecksFound.None, [], []));
        Assert.Equal("no check appeared on named within 60 s", (await Runner(harness, noneYet).RunAsync(Run(step with { Commit = "named", AppearSeconds = 600, WaitMinutes = 1 }, repository.Path), Token)).ChecksDetail);
        _ = await Runner(harness, noneYet).RunAsync(Run(step with { Commit = "named", TimeoutMinutes = null }, repository.Path), Token);
        await noneYet.Received(1).WaitAsync(repository.Path, Arg.Is<CiChecksRequest>(request => request.ReadTimeLimit == null), Arg.Any<CancellationToken>());
        Assert.Equal(("pending", "1 check(s) (deploy) still running on named after 1200 s"), (pending.Checks, pending.ChecksDetail));
        Assert.Equal(("unsettled", "1 check(s) (e2e) on named ended neither green nor red"), (unsettled.Checks, unsettled.ChecksDetail));
        Assert.All([acceptedNone, acceptedPending, acceptedGreen], accepted => Assert.Equal((true, HarnessExit.Success, null), (accepted.Passed, accepted.Exit, accepted.Checks)));
        Assert.Equal("""{"passed":false,"commit":"named","checks":[],"red":[],"pending":[],"unsettled":[]}""", acceptedNone.Output!.ToJsonString());
        Assert.Equal(
            """{"passed":false,"commit":"named","checks":[{"name":"build","state":"green","conclusion":"success","url":"https://ci/build"},{"name":"deploy","state":"pending","conclusion":null,"url":"https://ci/deploy"}],"red":[],"pending":["deploy"],"unsettled":[]}""",
            acceptedPending.Output!.ToJsonString());
        Assert.True(acceptedGreen.Output!["passed"]!.GetValue<bool>());
        Assert.Equal((true, true, CiExit.LegRed, "1 check(s) (license) red on named"), (acceptedRed.Passed, acceptedRed.Red, acceptedRed.Exit, acceptedRed.Error));
        await checks.Received(8).WaitAsync(Arg.Any<string>(), Arg.Any<CiChecksRequest>(), Arg.Any<CancellationToken>());
        await checks.Received(4).WaitAsync(
            repository.Path,
            Arg.Is<CiChecksRequest>(request => request.Commit == "named" && request.Once),
            Arg.Any<CancellationToken>());
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("ci-wait")!.Output!)!, passed.Output));
    }

    /// <summary>
    /// A red check ends the wait red (1): each answered with where it failed, and of a job its whole log kept in the step's
    /// directory, named for the job and its id - two jobs of one name keep one each - and its last lines in tail, both masked
    /// by the repository's runner secrets, each attempt's logs its own; what could not be read of it said in unread; a red
    /// status, no job, with its text and no log.
    /// </summary>
    [Fact]
    public async Task ARedCheck_IsAnsweredWithWhereItFailed_ItsLogKeptMasked()
    {
        var (harness, repository) = await RepositoryAsync();
        repository.WriteFile(Path.Combine(".harness-config", "runner", ".secrets", "ci.env"), "TOKEN=hush-123\n");
        var layout = OrchestratorLayout.Of((await harness.ContextLoader.LoadAsync(repository.Path, Token)).Layout, "r1");
        var stale = Path.Combine(layout.InstanceDirectory("wait", null), OrchestratorLayout.StepLogsDirectoryName, "gone.log");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "from the attempt before\n");
        var log = string.Join("\n", Enumerable.Range(1, 60).Select(line => $"line {line}")) + "\nerror: token hush-123 refused\n";
        var job = new CiCheck("test (linux / x64)", CiCheckState.Red, "failure", "https://ci/test", 7, 70, null);
        var status = new CiCheck("license", CiCheckState.Red, "error", "https://ci/license", null, null, "no header");
        var twin = new CiCheck("test (linux / x64)", CiCheckState.Red, "failure", "https://ci/test-docs", 8, 80, null);
        var checks = Substitute.For<ICiChecksService>();
        checks.WaitAsync(Arg.Any<string>(), Arg.Any<CiChecksRequest>(), Arg.Any<CancellationToken>()).Returns(
            new CiChecksReading(
                "c1",
                CiChecksFound.Red,
                [job, status, twin],
                [
                    new CiRedCheck(job, "test (linux / x64)", "Pipeline", "Test", "linux", true, log, null),
                    new CiRedCheck(status, null, null, null, null, false, null, null),
                    new CiRedCheck(twin, "test (linux / x64)", null, null, null, false, "docs log\n", "Its run was not read: HTTP 502"),
                ]));

        var result = await Runner(harness, checks).RunAsync(Run(new WorkflowStep { Name = "wait", Type = "ci-wait", Commit = "c1" }, repository.Path), Token);

        Assert.Equal((true, true, CiExit.LegRed), (result.Passed, result.Red, result.Exit));
        Assert.Equal("3 check(s) (test (linux / x64), license, test (linux / x64)) red on c1", result.Error);
        var red = result.Output!["red"]!.AsArray();
        var kept = Path.Combine(layout.InstanceDirectory("wait", null), OrchestratorLayout.StepLogsDirectoryName, "test (linux - x64).70.log");
        var twinKept = Path.Combine(layout.InstanceDirectory("wait", null), OrchestratorLayout.StepLogsDirectoryName, "test (linux - x64).80.log");
        Assert.Equal(kept, red[0]!["log"]!.GetValue<string>());
        Assert.Equal(log.Replace("hush-123", ActionValues.Mask, StringComparison.Ordinal), File.ReadAllText(kept));
        Assert.Equal(50, red[0]!["tail"]!.AsArray().Count);
        Assert.Equal($"error: token {ActionValues.Mask} refused", red[0]!["tail"]!.AsArray()[^1]!.GetValue<string>());
        Assert.Equal(("Pipeline", "Test", "linux", true), (red[0]!["workflow"]!.GetValue<string>(), red[0]!["step"]!.GetValue<string>(), red[0]!["leg"]!.GetValue<string>(), red[0]!["overran"]!.GetValue<bool>()));
        Assert.Equal(("license", "error", "no header"), (red[1]!["name"]!.GetValue<string>(), red[1]!["conclusion"]!.GetValue<string>(), red[1]!["description"]!.GetValue<string>()));
        Assert.Null(red[1]!["log"]);
        Assert.Null(red[0]!["unread"]);
        Assert.Equal((twinKept, "docs log\n", "Its run was not read: HTTP 502"), (red[2]!["log"]!.GetValue<string>(), File.ReadAllText(twinKept), red[2]!["unread"]!.GetValue<string>()));
        Assert.False(File.Exists(stale));
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("ci-wait")!.Output!)!, result.Output));
    }

    /// <summary>
    /// Rows or paths given as an expression the engine handed nothing for are its own fault (70) - never read as naming
    /// nothing, which would judge nothing and pass.
    /// </summary>
    [Fact]
    public async Task AnExpressionTheEngineHandedNothingFor_IsItsOwnFault()
    {
        var (harness, repository) = await RepositoryAsync();
        var balance = Substitute.For<IAnchorBalanceService>();
        balance.CheckAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Balance());
        var citations = Substitute.For<IAnchorCitationService>();
        citations.CheckAsync(Arg.Any<string>(), Arg.Any<AnchorCitationSubject>(), Arg.Any<CancellationToken>())
            .Returns(new AnchorCitationReport(AnchorCitationSubject.CurrentTree, "the tree", ["src"], 1, 0, 0, []));
        var rows = new WorkflowStep { Name = "balance", Type = "anchor-balance", Rows = new FlowList([], "fold.rows") };
        var paths = new WorkflowStep { Name = "cites", Type = "anchor-citations", AnchorPaths = new FlowList([], "fold.paths") };

        var unread = await Assert.ThrowsAsync<HarnessException>(
            () => new AnchorBalanceStepRunner(harness.ContextLoader, harness.Platform, balance).RunAsync(Run(rows, repository.Path), Token));
        var notNames = await Assert.ThrowsAsync<HarnessException>(
            () => new AnchorCitationsStepRunner(harness.ContextLoader, harness.Platform, citations).RunAsync(
                Run(paths, repository.Path, read: new Dictionary<string, FlowValue> { ["paths"] = FlowValue.Of(new JsonArray(1, 2)) }),
                Token));

        Assert.Equal((HarnessExit.InternalError, "'balance' was handed no names its rows read where it started"), (unread.ExitCode, unread.Message));
        Assert.Equal((HarnessExit.InternalError, "'cites' was handed no names its paths read where it started"), (notNames.ExitCode, notNames.Message));
    }

    /// <summary>What an anchor check answered: whether it passed, and each finding's line, joined by " | ".</summary>
    private static (bool Passed, string Findings) Findings(FlowStepResult result)
        => (result.Output!["passed"]!.GetValue<bool>(), string.Join(" | ", result.Output!["findings"]!.AsArray().Select(line => line!.GetValue<string>())));

    private static CiWaitStepRunner Runner(HarnessFactory harness, ICiChecksService checks)
        => new(harness.ContextLoader, harness.Platform, harness.FileSystem, new ActionValuesReader(harness.FileSystem, harness.Output), checks);

    /// <summary>
    /// A balance that opened A-TWO and A-THREE, closed A-ONE, and holds a problem at row A-FOUR's line and one of the
    /// registry as a whole - a fatal one, or a note, as <paramref name="registryWide"/> says.
    /// </summary>
    private static AnchorBalanceReport Balance(AnchorFindingSeverity registryWide = AnchorFindingSeverity.Fatal)
        => new(
            "origin/main",
            "c0",
            "c0",
            [],
            2,
            3,
            [new AnchorClosing("A-ONE", Bookkeeping: false)],
            [],
            [new AnchorOpening("A-TWO", "does x", Disclosed: false), new AnchorOpening("A-THREE", "does y", Disclosed: false)],
            [],
            [],
            [
                new AnchorFinding("anchors/pending.md", 7, AnchorFindingSeverity.Fatal, "row A-FOUR cannot be read") { RowId = "A-FOUR" },
                new AnchorFinding("anchors/pending.md", 0, registryWide, "the registry has no header"),
            ]);

    private static FlowStepRun Run(WorkflowStep step, string repository, IReadOnlyDictionary<string, FlowValue>? read = null, IReadOnlyList<FlowAnswerGiven>? answers = null)
        => new(step, step.Name, 1, FlowValue.Missing, null, null, answers ?? [], new NoEvents(), Place: new FlowRunPlace(repository, "r1", null, Flow), Read: read);

    /// <summary>A repository with the harness initialised, committed, on branch 'work'.</summary>
    private async Task<(HarnessFactory Harness, TempDirectory Repository)> RepositoryAsync()
    {
        var harness = new HarnessFactory();
        var repository = new TempDirectory();
        _made.Add(repository);

        repository.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(repository.Path, Token, new HarnessConfig());
        await harness.CommitAllAsync(repository.Path, "initial", Token);
        await harness.RunGitAsync(repository.Path, ["checkout", "--quiet", "-b", "work"], Token);

        return (harness, repository);
    }

    /// <summary>A step's events, which no check that runs no legs says.</summary>
    private sealed class NoEvents : IFlowRunEvents
    {
        public void LegPlaced(string leg, bool inACopy) => throw new InvalidOperationException("a check places no leg");

        public void EveryLegPlaced() => throw new InvalidOperationException("a check places no leg");

        public void LegCopyMade(string leg) => throw new InvalidOperationException("a check places no leg");

        public void LegEnded(string leg) => throw new InvalidOperationException("a check places no leg");

        public void Used(long tokens, double usd) => throw new InvalidOperationException("a check spends nothing");

        public void HandoffTaken() => throw new InvalidOperationException("a check takes no handoff");
    }
}
