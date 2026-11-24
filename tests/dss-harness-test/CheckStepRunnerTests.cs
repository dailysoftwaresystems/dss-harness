using System.Text.Json.Nodes;
using DssHarness.Core.Anchors;
using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Pages;
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
    /// directory, named for the job and its id - two jobs of one name keep one each - with how many lines it holds and the
    /// page where it failed, a log of one page given whole, each line by its number, both masked by the repository's runner
    /// secrets, each attempt's logs its own; what could not be read of it said in unread; a red status, no job, with its
    /// text and no log.
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
        Assert.Equal(
            Enumerable.Range(1, 60).Select(line => $"{line}\tline {line}").Append($"61\terror: token {ActionValues.Mask} refused").ToArray(),
            red[0]!["failing"]!.AsArray().Select(line => line!.GetValue<string>()).ToArray());
        Assert.Equal(61L, red[0]!["lines"]!.GetValue<long>());
        Assert.Equal(("Pipeline", "Test", "linux", true), (red[0]!["workflow"]!.GetValue<string>(), red[0]!["step"]!.GetValue<string>(), red[0]!["leg"]!.GetValue<string>(), red[0]!["overran"]!.GetValue<bool>()));
        Assert.Equal(("license", "error", "no header"), (red[1]!["name"]!.GetValue<string>(), red[1]!["conclusion"]!.GetValue<string>(), red[1]!["description"]!.GetValue<string>()));
        Assert.Null(red[1]!["log"]);
        Assert.Null(red[1]!["lines"]);
        Assert.Empty(red[1]!["failing"]!.AsArray());
        Assert.Null(red[0]!["unread"]);
        Assert.Equal((twinKept, "docs log\n", "Its run was not read: HTTP 502"), (red[2]!["log"]!.GetValue<string>(), File.ReadAllText(twinKept), red[2]!["unread"]!.GetValue<string>()));
        Assert.False(File.Exists(stale));
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("ci-wait")!.Output!)!, result.Output));
    }

    /// <summary>
    /// A red job's log is given by reference - kept whole, its lines counted - with the page of it where the job failed
    /// first: the page holding the last line GitHub marks an error, though three thousand lines of clean-up follow it, each
    /// line by its number - never the log's last lines alone, which would be the clean-up's and none of the failure.
    /// </summary>
    [Fact]
    public async Task ARedCheck_GivesItsLogByReference()
    {
        var (harness, repository) = await RepositoryAsync();
        var layout = OrchestratorLayout.Of((await harness.ContextLoader.LoadAsync(repository.Path, Token)).Layout, "r1");
        var log = string.Join(
            "\n",
            Enumerable.Range(1, 3000).Select(line => $"compiling unit {line}")
                .Append("error CS1002: ; expected")
                .Append($"{CiWaitStepRunner.ErrorMark}Process completed with exit code 1.")
                .Concat(Enumerable.Range(1, 3000).Select(line => $"Post job cleanup {line}"))) + "\n";
        var job = new CiCheck("build", CiCheckState.Red, "failure", "https://ci/build", 7, 70, null);
        var checks = Substitute.For<ICiChecksService>();
        checks.WaitAsync(Arg.Any<string>(), Arg.Any<CiChecksRequest>(), Arg.Any<CancellationToken>()).Returns(
            new CiChecksReading("c1", CiChecksFound.Red, [job], [new CiRedCheck(job, "build", "Pipeline", "Build", null, false, log, null)]));

        var result = await Runner(harness, checks).RunAsync(Run(new WorkflowStep { Name = "wait", Type = "ci-wait", Commit = "c1" }, repository.Path), Token);

        var red = Assert.Single(result.Output!["red"]!.AsArray())!;
        var kept = Path.Combine(layout.InstanceDirectory("wait", null), OrchestratorLayout.StepLogsDirectoryName, "build.70.log");
        var failing = red["failing"]!.AsArray().Select(line => line!.GetValue<string>()).ToArray();

        Assert.Equal(kept, red["log"]!.GetValue<string>());
        Assert.Equal(log, File.ReadAllText(kept));
        Assert.Equal(6002L, red["lines"]!.GetValue<long>());
        Assert.Contains("3001\terror CS1002: ; expected", failing);
        Assert.Contains($"3002\t{CiWaitStepRunner.ErrorMark}Process completed with exit code 1.", failing);
        Assert.DoesNotContain("6002\tPost job cleanup 3000", failing);

        // The page the kept log's pages hold there, as the one owner of a text's pages lays them: more than one page, and
        // not the last.
        var pages = await TextPages.LayAsync(harness.FileSystem, kept, "log", "the log", CiWaitStepRunner.LogPage, numbered: true, Token);
        var holding = Enumerable.Range(1, pages.Marks.Count).Last(page => pages.Marks[page - 1].FirstLine <= 3002);
        var page = await TextPages.PageAsync(harness.FileSystem, pages, holding, numbered: true, note: null, Token);

        Assert.True(holding < pages.Marks.Count, $"page {holding} of {pages.Marks.Count}");
        Assert.Equal(page.Entries.Select(entry => entry.Text).ToArray(), failing);

        // The digest holds no error a tool's form read, so the page goes whole beside it, and the lines no parser held -
        // each on the page, numbered - are said there alone: every line reaches a model once.
        var digest = red["digest"]!.AsObject();

        Assert.False(digest.ContainsKey("unparsed"));
        Assert.False(digest.ContainsKey("unparsedLeftOut"));
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("ci-wait")!.Output!)!, result.Output));
    }

    /// <summary>
    /// A red job is digested from its annotations, read from the forge as data, and its kept log in the forms GitHub
    /// writes it: the byte order mark, the timestamps and the group markers taken off, every line numbered as the kept
    /// file numbers it. A problem matcher's line - under the forge's marker in the log, an annotation with its place on the
    /// forge - is one finding, first, named by its path in the repository; a workflow command's annotation takes its
    /// line of the log; and a line the forge marked that no form read - the runner's own sentence that the step failed -
    /// is said among the lines no parser held, never as the first error. The raw page goes, the typed errors standing and
    /// the line no parser held said in the digest alone.
    /// </summary>
    [Fact]
    public async Task ARedJob_IsDigestedFromItsAnnotationsAndItsLog_EachFindingOnce_NumberedAsTheKeptLog()
    {
        var (harness, repository) = await RepositoryAsync();
        var layout = OrchestratorLayout.Of((await harness.ContextLoader.LoadAsync(repository.Path, Token)).Layout, "r1");
        var log = "\uFEFF" + string.Join(
            "\n",
            "2026-11-23T00:39:53.9245022Z ##[group]Run dotnet build",
            "2026-11-23T00:39:53.9300000Z \u001b[36;1mdotnet build\u001b[0m",
            "2026-11-23T00:39:54.1000000Z ##[endgroup]",
            "2026-11-23T00:40:00.0000000Z   Determining projects to restore...",
            "2026-11-23T00:40:01.0000000Z ##[error]/home/runner/work/app/app/src/Program.cs(12,35): error CS0103: The name 'x' does not exist in the current context [/home/runner/work/app/app/src/App.csproj]",
            "2026-11-23T00:40:01.5000000Z ##[error]Missing semicolon",
            "2026-11-23T00:40:02.0000000Z ##[error]Process completed with exit code 1.") + "\n";
        var job = new CiCheck("build", CiCheckState.Red, "failure", "https://ci/build", 7, 70, null) { CheckRunId = 70, Annotations = 3, Repository = "app" };
        CiAnnotation[] annotations =
        [
            new("src/Program.cs", 12, 12, 35, 35, "failure", null, "The name 'x' does not exist in the current context", null),
            new("src/app.ts", 4, 4, null, null, "failure", "Lint", "Missing semicolon", null),
            new(null, null, null, null, null, "failure", null, "Process completed with exit code 1.", null),
        ];
        var checks = Substitute.For<ICiChecksService>();
        checks.WaitAsync(Arg.Any<string>(), Arg.Any<CiChecksRequest>(), Arg.Any<CancellationToken>()).Returns(
            new CiChecksReading("c1", CiChecksFound.Red, [job], [new CiRedCheck(job, "build", "Pipeline", "Build", null, false, log, null) { Annotations = annotations }]));

        var result = await Runner(harness, checks).RunAsync(Run(new WorkflowStep { Name = "wait", Type = "ci-wait", Commit = "c1" }, repository.Path), Token);

        var red = Assert.Single(result.Output!["red"]!.AsArray())!;
        var digest = red["digest"]!.AsObject();
        var kept = File.ReadAllLines(Path.Combine(layout.InstanceDirectory("wait", null), OrchestratorLayout.StepLogsDirectoryName, "build.70.log"));

        // Two errors, each once: the matcher's line read by its own form and by the forge is one finding, and so is the
        // workflow command's; the runner's sentence is no error of a tool's. A count of none is no member of it.
        Assert.Equal(2, digest["errors"]!.GetValue<int>());
        Assert.False(digest.ContainsKey("warnings"));

        var first = digest["first"]!.AsObject();

        Assert.Equal(
            ("msbuild", "CS0103", "src/Program.cs", 12, 35, "msbuild CS0103 src/Program.cs(12,35): The name 'x' does not exist in the current context"),
            (first["tool"]!.GetValue<string>(), first["code"]!.GetValue<string>(), first["file"]!.GetValue<string>(), first["line"]!.GetValue<int>(), first["column"]!.GetValue<int>(), first["said"]!.GetValue<string>()));
        Assert.Equal([5, 5], first["log"]!.AsArray().Select(line => line!.GetValue<int>()));
        Assert.StartsWith("2026-11-23T00:40:01.0000000Z ##[error]/home/runner/work/app/app/src/Program.cs(12,35): error CS0103", kept[4], StringComparison.Ordinal);

        var next = Assert.Single(digest["diagnostics"]!.AsArray())!;

        Assert.Equal("github-actions src/app.ts(4): Lint: Missing semicolon", next["said"]!.GetValue<string>());
        Assert.Equal([6, 6], next["log"]!.AsArray().Select(line => line!.GetValue<int>()));
        Assert.Equal(["Process completed with exit code 1."], digest["unparsed"]!.AsArray().Select(line => line!.GetValue<string>()));

        // The typed errors stand, and the line no parser held is said in the digest alone - never the raw page, nor the line
        // again beside it.
        Assert.Empty(red["failing"]!.AsArray());
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("ci-wait")!.Output!)!, result.Output));
    }

    /// <summary>
    /// A red check no workflow run made has no log, and is its annotations alone: one with a place a diagnostic, one with
    /// none a sentence said among the lines no parser held.
    /// </summary>
    [Fact]
    public async Task ARedCheckWithNoLog_IsDigestedFromItsAnnotationsAlone()
    {
        var (harness, repository) = await RepositoryAsync();
        var app = new CiCheck("coverage", CiCheckState.Red, "failure", "https://checks/coverage", null, null, null) { CheckRunId = 90, Annotations = 2 };
        var checks = Substitute.For<ICiChecksService>();
        checks.WaitAsync(Arg.Any<string>(), Arg.Any<CiChecksRequest>(), Arg.Any<CancellationToken>()).Returns(
            new CiChecksReading("c1", CiChecksFound.Red, [app], [new CiRedCheck(app, null, null, null, null, false, null, null)
            {
                Annotations = [new("src/Rates.cs", 30, 41, null, null, "warning", null, "These lines are not covered", "11 lines"), new(null, null, null, null, null, "failure", null, "Coverage fell below 80%", null)],
            }]));

        var result = await Runner(harness, checks).RunAsync(Run(new WorkflowStep { Name = "wait", Type = "ci-wait", Commit = "c1" }, repository.Path), Token);

        var digest = Assert.Single(result.Output!["red"]!.AsArray())!["digest"]!.AsObject();

        Assert.Equal(1, digest["warnings"]!.GetValue<int>());
        Assert.False(digest.ContainsKey("errors"));
        Assert.False(digest.ContainsKey("first"));
        Assert.Equal([("src/Rates.cs", 1)], digest["byFile"]!.AsArray().Select(entry => (entry!["of"]!.GetValue<string>(), entry["count"]!.GetValue<int>())));
        Assert.Equal(["Coverage fell below 80%"], digest["unparsed"]!.AsArray().Select(line => line!.GetValue<string>()));
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
    /// <summary>
    /// The <c>class-closure</c> check judges the builder's record against the classes the plan declares, the item's
    /// stored streams and the agent's worktree, with no model: a search cited that no call of the streams made, a
    /// neighbour measured by no measure call, a deleted site that still declares its symbol, a site that reads its home
    /// where the agent changed nothing, a pin that names a test the worktree does not declare, a claim that names no
    /// axis, and a class with no entry at all - each a problem the fold waits behind, exit 1. A record that holds all of
    /// it passes, its touches stated.
    /// </summary>
    [Fact]
    public async Task TheClassClosureCheck_JudgesTheRecordAgainstTheClassesTheStreamsAndTheTree()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("builder");
        var layout = kit.Layout;

        // The home still declares its symbol in the worktree (text, for the run made no graph), and the pin's one test
        // is named there too - the second is not.
        Directory.CreateDirectory(Path.Combine(worktree, "src"));
        File.WriteAllText(Path.Combine(worktree, "src", "Home.cs"), "class Home { public int Plan() => 1; } // tests: PlanTest, OtherTest\n");
        File.WriteAllText(Path.Combine(worktree, "src", "Old.cs"), "class Old { public void Site() { } }\n");

        // One search made as a call of the stored stream, its input holding its query; one measurement by the measure
        // tool; and a search and a measurement the record cites that no call made.
        WriteStream(
            kit.Harness,
            layout,
            "builder",
            [
                new DssHarness.Core.Agents.ToolCallStarted("call-1", "graph", DssHarness.Core.Agents.AgentToolRunner.Harness),
                new DssHarness.Core.Agents.ToolCallFinished("call-1", Ok: true, TimeSpan.Zero, """{"query":"turnover:","operation":"find"}""", "found 3"),
                new DssHarness.Core.Agents.ToolCallStarted("call-2", "measure", DssHarness.Core.Agents.AgentToolRunner.Harness),
                new DssHarness.Core.Agents.ToolCallFinished("call-2", Ok: true, TimeSpan.Zero, """{"leg":"windows","cell":"turnover:x"}""", "passed"),
            ]);
        WriteMeasure(layout, "builder", "impl-1", "call-2", "turnover:x", Measured("passed"));

        var sync = SyncKit.Service(kit.Harness);
        var agents = Substitute.For<DssHarness.Core.Orchestration.IAgentService>();
        agents.FoldAsync(Arg.Any<DssHarness.Core.Orchestration.FoldAgentRequest>(), Arg.Any<CancellationToken>())
            .Returns(DssHarness.Core.Application.UseCaseResult<DssHarness.Core.Orchestration.AgentVerbFacts>.From(
                new CommandOutcome(HarnessExit.Success, "dry run") { Facts = new DssHarness.Core.Orchestration.AgentVerbFacts(0, "dry run", false, null, null, [], [], []) { Planned = new DssHarness.Core.Orchestration.AgentPlanned([], [], [], [], null) { Written = ["src/Home.cs"] } } }));
        var graphs = Substitute.For<DssHarness.Core.Codebase.ICodebaseGraphService>();

        var result = await new ClassClosureStepRunner(
            kit.Harness.ContextLoader,
            kit.Harness.Platform,
            kit.Harness.OrchestrationStore,
            kit.Harness.FileSystem,
            agents,
            graphs,
            kit.Harness.GitClient,
            new DssHarness.Core.Mutations.ArmListingReader(sync, kit.Harness.FileSystem, kit.Harness.Platform)).RunAsync(
            Run(
                new WorkflowStep { Name = "class-closure", Type = "class-closure", In = "item-agent", Base = "HEAD" },
                kit.Main,
                read: new Dictionary<string, FlowValue>
                {
                    ["classes"] = FlowValue.Of(new JsonArray(Class("c-rate", home: "src/Home.cs", symbol: "Home", site: "src/Old.cs"))),
                    ["record"] = FlowValue.Of(new JsonArray(
                        Entry(
                            "c-rate",
                            home: "src/Home.cs",
                            sites: [("src/Old.cs", "Old", "deleted"), ("src/MadeUp.cs", "Made", "reads-home")],
                            searches: [("the turnover", "turnover:", "call-1"), ("the references", "Home", "call-9")],
                            neighbours: [("turnover:x", "call-2", "passed", "")],
                            tests: ["PlanTest", "NopeTest"],
                            axes: ["format"],
                            claim: "The rate holds over every form"))),
                },
                worksIn: "builder",
                instance: "rounds#1.items[0].class-closure",
                run: OrchestrationKit.Orchestrator),
            Token);

        Assert.False(result.Output!["passed"]!.GetValue<bool>());
        Assert.Equal(ClassClosureExit.Findings, result.Exit);
        Assert.Equal(
            [
                "class 'c-rate': the site 'src/Old.cs' is deleted, but it still declares 'Old'",
                "class 'c-rate': the site 'src/MadeUp.cs' reads its home, but the agent changed nothing at it",
                "class 'c-rate': the search for 'the references' cites the call 'call-9', which is not a tool call of this item's stored streams",
                "class 'c-rate': the test 'NopeTest' of the pin is not declared in the worktree",
                "class 'c-rate': the claim does not name the axis 'format'",
            ],
            [.. result.Output["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);
        Assert.Empty(result.Output["hotspots"]!.AsArray());
        Assert.Equal("src/Home.cs", result.Output["touches"]!.AsArray()[0]!["path"]!.GetValue<string>());

        // The record complete - every planned site accounted for, the searches made, the pin declared, the claim naming
        // the universe - passes, its touches stated and nothing hot.
        var passing = await new ClassClosureStepRunner(
            kit.Harness.ContextLoader,
            kit.Harness.Platform,
            kit.Harness.OrchestrationStore,
            kit.Harness.FileSystem,
            agents,
            graphs,
            kit.Harness.GitClient,
            new DssHarness.Core.Mutations.ArmListingReader(sync, kit.Harness.FileSystem, kit.Harness.Platform)).RunAsync(
            Run(
                new WorkflowStep { Name = "class-closure", Type = "class-closure", In = "item-agent", Base = "HEAD" },
                kit.Main,
                read: new Dictionary<string, FlowValue>
                {
                    ["classes"] = FlowValue.Of(new JsonArray(Class("c-rate", home: "src/Home.cs", symbol: "Home", site: "src/Old.cs"))),
                    ["record"] = FlowValue.Of(new JsonArray(
                        Entry(
                            "c-rate",
                            home: "src/Home.cs",
                            sites: [("src/Old.cs", "Old", "deleted"), ("src/Home.cs", "Home", "home")],
                            searches: [("the turnover", "turnover:", "call-1")],
                            neighbours: [("turnover:x", "call-2", "passed", "")],
                            tests: ["PlanTest"],
                            axes: ["format"],
                            claim: "The rate holds over every format"))),
                },
                worksIn: "builder",
                instance: "rounds#1.items[0].class-closure",
                run: OrchestrationKit.Orchestrator),
            Token);

        // The site deleted above still declares its symbol in the worktree, so the complete record still fails on it, and
        // on it alone - the deleted file's text has to go for the record to pass; here it does.
        Assert.False(passing.Output!["passed"]!.GetValue<bool>());
        Assert.Equal(ClassClosureExit.Findings, passing.Exit);
        Assert.Equal(
            ["class 'c-rate': the site 'src/Old.cs' is deleted, but it still declares 'Old'"],
            [.. passing.Output["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);

        File.Delete(Path.Combine(worktree, "src", "Old.cs"));

        var passed = await new ClassClosureStepRunner(
            kit.Harness.ContextLoader,
            kit.Harness.Platform,
            kit.Harness.OrchestrationStore,
            kit.Harness.FileSystem,
            agents,
            graphs,
            kit.Harness.GitClient,
            new DssHarness.Core.Mutations.ArmListingReader(sync, kit.Harness.FileSystem, kit.Harness.Platform)).RunAsync(
            Run(
                new WorkflowStep { Name = "class-closure", Type = "class-closure", In = "item-agent", Base = "HEAD" },
                kit.Main,
                read: new Dictionary<string, FlowValue>
                {
                    ["classes"] = FlowValue.Of(new JsonArray(Class("c-rate", home: "src/Home.cs", symbol: "Home", site: "src/Old.cs"))),
                    ["record"] = FlowValue.Of(new JsonArray(
                        Entry(
                            "c-rate",
                            home: "src/Home.cs",
                            sites: [("src/Old.cs", "Old", "deleted"), ("src/Home.cs", "Home", "home")],
                            searches: [("the turnover", "turnover:", "call-1")],
                            neighbours: [("turnover:x", "call-2", "passed", "")],
                            tests: ["PlanTest"],
                            axes: ["format"],
                            claim: "The rate holds over every format"))),
                },
                worksIn: "builder",
                instance: "rounds#1.items[0].class-closure",
                run: OrchestrationKit.Orchestrator),
            Token);

        Assert.True(passed.Output!["passed"]!.GetValue<bool>());
        Assert.True(passed.Passed);
        Assert.Equal(HarnessExit.Success, passed.Exit);
    }

    /// <summary>
    /// The home's touches are counted since the base, the item's own commits left out - the fold ledger attributes them
    /// to instances of one item - so a rework of the item's own class after its first send-back is never a second hand,
    /// and another item's commit is. Where the record carries no home audit and the home was touched by other work, the
    /// check fails as a home audit (exit 2), its repair <c>home-audit</c> and not a closing of the record.
    /// </summary>
    [Fact]
    public async Task TheClassClosureCheck_CountsAHomeSTouches_LeavingOutTheItemsOwnCommits()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("builder");
        var layout = kit.Layout;
        var baseCommit = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;

        // Another item's commit touches the home; the item's own fold, committed, touches it again.
        Directory.CreateDirectory(Path.Combine(kit.Main, "src"));
        File.WriteAllText(Path.Combine(kit.Main, "src", "Home.cs"), "one\n");
        await kit.Harness.CommitAllAsync(kit.Main, "another item", Token);
        var other = await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token);
        File.WriteAllText(Path.Combine(kit.Main, "src", "Home.cs"), "two\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the item's own", Token);
        var own = await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token);

        kit.Harness.OrchestrationStore.UpdateFolds(layout, ledger => ledger with
        {
            Folds = [new FlowFoldEntry("f1", "rounds#1.items[0].fold", 1, "fold", "builder", FlowFoldEntry.Intended, DateTimeOffset.UtcNow, [new FlowFoldPath("src/Home.cs", "written", null)], [])],
            Commits = [new FlowCommitEntry("c1", "rounds#1.items[0].checkpoint", 1, FlowCommitEntry.Committed, null, [new FlowCommitPath("src/Home.cs", null)], ["f1"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Sha = own }],
        });

        var sync = SyncKit.Service(kit.Harness);
        var agents = Substitute.For<DssHarness.Core.Orchestration.IAgentService>();
        agents.FoldAsync(Arg.Any<DssHarness.Core.Orchestration.FoldAgentRequest>(), Arg.Any<CancellationToken>())
            .Returns(DssHarness.Core.Application.UseCaseResult<DssHarness.Core.Orchestration.AgentVerbFacts>.From(
                new CommandOutcome(HarnessExit.Success, "dry run") { Facts = new DssHarness.Core.Orchestration.AgentVerbFacts(0, "dry run", false, null, null, [], [], []) { Planned = new DssHarness.Core.Orchestration.AgentPlanned([], [], [], [], null) } }));
        var graphs = Substitute.For<DssHarness.Core.Codebase.ICodebaseGraphService>();

        var result = await ClosureAsync(kit, agents, graphs, baseCommit);

        Assert.False(result.Output!["passed"]!.GetValue<bool>());
        Assert.Equal(ClassClosureExit.HomeAudit, result.Exit);
        Assert.Equal(
            ["class 'c-rate': home touched 1 times in this run by other work"],
            [.. result.Output["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);
        var touch = result.Output["touches"]!.AsArray().Single();
        Assert.Equal(("src/Home.cs", 1, 2), (touch!["path"]!.GetValue<string>(), touch["byOthers"]!.GetValue<int>(), touch["all"]!.GetValue<int>()));

        // The record that carries its home audit passes: the audit is what the touches asked for.
        var audited = await ClosureAsync(kit, agents, graphs, baseCommit, homeAudit: true);

        Assert.True(audited.Output!["passed"]!.GetValue<bool>());

        // Outside every forEach a step is an item of itself: a fold no forEach holds is another step's work, never the
        // check's own, so both commits are other work's.
        kit.Harness.OrchestrationStore.UpdateFolds(layout, ledger => ledger with { Folds = [ledger.Folds[0] with { Instance = "notes-fold" }] });

        var outside = await ClosureAsync(kit, agents, graphs, baseCommit, instance: "class-closure");
        var counted = outside.Output!["touches"]!.AsArray().Single();

        Assert.Equal(("src/Home.cs", 2, 2), (counted!["path"]!.GetValue<string>(), counted["byOthers"]!.GetValue<int>(), counted["all"]!.GetValue<int>()));

        return;

        async Task<FlowStepResult> ClosureAsync(OrchestrationKit kit, DssHarness.Core.Orchestration.IAgentService agents, DssHarness.Core.Codebase.ICodebaseGraphService graphs, string from, bool homeAudit = false, string instance = "rounds#1.items[0].class-closure")
        {
            var entry = Entry("c-rate", home: "src/Home.cs", sites: [("src/Home.cs", "Home", "home")], searches: [("the data", "query", "call-1")], neighbours: [], tests: [], axes: ["format"], claim: "The rate holds over every format");
            var record = new JsonArray(entry);

            if (homeAudit)
            {
                entry["homeAudit"] = new JsonObject { ["consolidated"] = new JsonArray("the rate"), ["invariants"] = new JsonArray("one place"), ["structural"] = new JsonArray("Home holds it") };
            }

            await WriteStreamAsync();
            return await new ClassClosureStepRunner(
                kit.Harness.ContextLoader,
                kit.Harness.Platform,
                kit.Harness.OrchestrationStore,
                kit.Harness.FileSystem,
                agents,
                graphs,
                kit.Harness.GitClient,
                new DssHarness.Core.Mutations.ArmListingReader(sync, kit.Harness.FileSystem, kit.Harness.Platform)).RunAsync(
                Run(
                    new WorkflowStep { Name = "class-closure", Type = "class-closure", In = "item-agent", Base = from },
                    kit.Main,
                    read: new Dictionary<string, FlowValue>
                    {
                        ["classes"] = FlowValue.Of(new JsonArray(Class("c-rate", home: "src/Home.cs", symbol: "Home"))),
                        ["record"] = FlowValue.Of(record),
                    },
                    worksIn: "builder",
                    instance: instance,
                    run: OrchestrationKit.Orchestrator),
                Token);

            async Task WriteStreamAsync()
            {
                Directory.CreateDirectory(layout.AgentStepDirectory("builder", "impl-1"));
                await File.WriteAllTextAsync(
                    Path.Combine(layout.AgentStepDirectory("builder", "impl-1"), DssHarness.Core.Agents.StoredStream.FileName),
                    string.Join("\n",
                        System.Text.Json.JsonSerializer.SerializeToNode(new DssHarness.Core.Agents.ToolCallStarted("call-1", "graph", DssHarness.Core.Agents.AgentToolRunner.Harness), typeof(DssHarness.Core.Agents.AgentEvent), DssHarness.Core.FileSystem.JsonStateFile.LineOptions)!.ToJsonString(DssHarness.Core.FileSystem.JsonStateFile.LineOptions),
                        System.Text.Json.JsonSerializer.SerializeToNode(new DssHarness.Core.Agents.ToolCallFinished("call-1", Ok: true, TimeSpan.Zero, """{"query":"query"}""", "found"), typeof(DssHarness.Core.Agents.AgentEvent), DssHarness.Core.FileSystem.JsonStateFile.LineOptions)!.ToJsonString(DssHarness.Core.FileSystem.JsonStateFile.LineOptions)) + "\n");
            }
        }
    }

    /// <summary>
    /// The check reads the calls of every client's stored stream as that client writes them, by the one reading of a
    /// stream's calls: a turn that started two calls before either was answered is paired by the calls' ids, never by the
    /// order the answers came in; a call Claude Code made of the engine's tool server, stored as
    /// <c>mcp__dssharness__measure</c>, is the runtime's <c>measure</c>; and a query is looked for in what the model wrote,
    /// never in the input's JSON text, where a backslash is escaped and Claude Code escapes <c>&lt;</c> and <c>&amp;</c>.
    /// The dry weighing it reads is asked as a run's.
    /// </summary>
    [Fact]
    public async Task TheClassClosureCheck_ReadsEveryClientsCalls_ByTheirIds_AsTheRuntimeNamesThem_AsTheModelWroteThem()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("builder");

        // As Claude Code stores a call's input: the model's JSON written again, every character its encoder escapes escaped.
        var asked = System.Text.Json.Nodes.JsonNode.Parse("""{"pattern":"class\\s+Rate","glob":"IReadOnlyList<string> && more"}""")!.ToJsonString();
        Assert.Contains("\\u003C", asked, StringComparison.Ordinal);

        WriteStream(
            kit.Harness,
            kit.Layout,
            "builder",
            [
                new DssHarness.Core.Agents.ToolCallStarted("toolu_1", "Grep", DssHarness.Core.Agents.AgentToolRunner.ClaudeCode),
                new DssHarness.Core.Agents.ToolCallStarted("toolu_2", "mcp__dssharness__measure", DssHarness.Core.Agents.AgentToolRunner.Harness),
                new DssHarness.Core.Agents.ToolCallFinished("toolu_1", Ok: true, TimeSpan.Zero, asked, "src/Rate.cs"),
                new DssHarness.Core.Agents.ToolCallFinished("toolu_2", Ok: true, TimeSpan.Zero, """{"leg":"windows","cell":"rate:x"}""", "passed"),
            ]);
        WriteMeasure(kit.Layout, "builder", "impl-1", "toolu_2", "rate:x", Measured("passed"));

        var agents = Weighing("src/Home.cs");
        var result = await Closure(kit, agents).RunAsync(
            ClosureRun(
                kit,
                Class("c-rate", home: "src/Home.cs", symbol: "Home"),
                Entry(
                    "c-rate",
                    home: "src/Home.cs",
                    sites: [("src/Home.cs", "Home", "home")],
                    searches: [("the rate's declarations", "class\\s+Rate", "toolu_1"), ("the rate's readers", "IReadOnlyList<string> && more", "toolu_1")],
                    neighbours: [("rate:x", "toolu_2", "passed", "")],
                    tests: [],
                    axes: ["format"],
                    claim: "The rate holds over every format")),
            Token);

        Assert.Empty(result.Output!["problems"]!.AsArray());
        Assert.True(result.Passed);
        Assert.Equal(HarnessExit.Success, result.Exit);
        _ = await agents.Received(1).FoldAsync(Arg.Is<DssHarness.Core.Orchestration.FoldAgentRequest>(request => request.ByRun && !request.Apply), Arg.Any<CancellationToken>());

        // A call cited for a measurement that is no measure call, and a query no text of the cited call holds, are refused.
        var wrong = await Closure(kit, agents).RunAsync(
            ClosureRun(
                kit,
                Class("c-rate", home: "src/Home.cs", symbol: "Home"),
                Entry(
                    "c-rate",
                    home: "src/Home.cs",
                    sites: [("src/Home.cs", "Home", "home")],
                    searches: [("the rate's writers", "class Rate", "toolu_1")],
                    neighbours: [("rate:x", "toolu_1", "passed", "")],
                    tests: [],
                    axes: ["format"],
                    claim: "The rate holds over every format")),
            Token);

        Assert.Equal(
            [
                "class 'c-rate': the search for 'the rate's writers' cites the call 'toolu_1', whose input does not hold the query 'class Rate'",
                "class 'c-rate': the neighbour 'rate:x' is measured by the call 'toolu_1', which is not a measure call of this item's stored streams",
            ],
            [.. wrong.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);
    }

    /// <summary>
    /// A site that reads its home is among the paths the agent changed where the run's ledger says one of the agent's
    /// earlier folds changed it, though the fold no longer writes it - the main tree holds it as the agent does once it is
    /// folded, so a rework of the review loop that changes another file leaves the site the agent's work all the same; a
    /// fold of another agent's is no change of this one's.
    /// </summary>
    [Fact]
    public async Task ASiteTheAgentFoldedEarlier_IsAmongThePathsItChanged()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("builder");

        kit.Harness.OrchestrationStore.UpdateFolds(kit.Layout, ledger => ledger with
        {
            Folds =
            [
                new FlowFoldEntry("f1", "rounds#1.items[0].fold", 1, "fold", "builder", "applied", DateTimeOffset.UtcNow, [new FlowFoldPath("src/A.cs", "written", null, new string('a', 64), Changed: true)], [], DateTimeOffset.UtcNow),
                new FlowFoldEntry("f2", "rounds#1.items[1].fold", 1, "fold", "other", "applied", DateTimeOffset.UtcNow, [new FlowFoldPath("src/C.cs", "written", null, new string('c', 64), Changed: true)], [], DateTimeOffset.UtcNow),
            ],
        });

        var result = await Closure(kit, Weighing("src/B.cs")).RunAsync(
            ClosureRun(
                kit,
                Class("c-rate", home: "src/Home.cs", symbol: "Home"),
                Entry(
                    "c-rate",
                    home: "src/Home.cs",
                    sites: [("src/A.cs", "Read", "reads-home"), ("src/B.cs", "Read", "reads-home"), ("src/C.cs", "Read", "reads-home")],
                    searches: [],
                    neighbours: [],
                    tests: [],
                    axes: ["format"],
                    claim: "The rate holds over every format")),
            Token);

        Assert.Equal(
            ["class 'c-rate': the site 'src/C.cs' reads its home, but the agent changed nothing at it"],
            [.. result.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);
    }

    /// <summary>
    /// What the check cannot weigh it never reads as nothing: where the fold's own dry weighing - asked as a run's - throws,
    /// or answers with no plan - an agent it cannot fold - the step fails with the weighing's own code and words, and no
    /// site that reads its home passes unchecked.
    /// </summary>
    [Fact]
    public async Task TheClassClosureCheck_FailsWithTheWeighingsOwnWords_WhereTheFoldCannotBeWeighed()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("builder");
        var run = ClosureRun(
            kit,
            Class("c-rate", home: "src/Home.cs", symbol: "Home"),
            Entry("c-rate", home: "src/Home.cs", sites: [("src/A.cs", "Read", "reads-home")], searches: [], neighbours: [], tests: [], axes: ["format"], claim: "The rate holds over every format"));

        var throwing = Substitute.For<DssHarness.Core.Orchestration.IAgentService>();
        throwing.FoldAsync(Arg.Any<DssHarness.Core.Orchestration.FoldAgentRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DssHarness.Core.Application.UseCaseResult<DssHarness.Core.Orchestration.AgentVerbFacts>>(new HarnessException(HarnessExit.CommandFailed, "git could not read the worktree.")));

        var thrown = await Closure(kit, throwing).RunAsync(run, Token);

        Assert.Equal((false, HarnessExit.CommandFailed, "the paths agent 'builder' changed could not be weighed: git could not read the worktree."), (thrown.Passed, thrown.Exit, thrown.Error));
        Assert.Null(thrown.Output);

        // The weighing is the fold's dry run, asked as a run's: no closure.json is asked of it.
        _ = await throwing.Received(1).FoldAsync(Arg.Is<DssHarness.Core.Orchestration.FoldAgentRequest>(request => !request.Apply && request.ByRun), Arg.Any<CancellationToken>());

        var unweighed = Substitute.For<DssHarness.Core.Orchestration.IAgentService>();
        unweighed.FoldAsync(Arg.Any<DssHarness.Core.Orchestration.FoldAgentRequest>(), Arg.Any<CancellationToken>())
            .Returns(DssHarness.Core.Application.UseCaseResult<DssHarness.Core.Orchestration.AgentVerbFacts>.From(
                new CommandOutcome(HarnessExit.Refused, "agent 'builder' cannot be folded: its HEAD was moved by hand.")));

        var refused = await Closure(kit, unweighed).RunAsync(run, Token);

        Assert.Equal((false, HarnessExit.Refused, "the paths agent 'builder' changed could not be weighed: agent 'builder' cannot be folded: its HEAD was moved by hand."), (refused.Passed, refused.Exit, refused.Error));
    }

    /// <summary>
    /// A class's arm is judged against the registry the agent's worktree holds, where an arm the work adds stands before
    /// the fold merges it into the main tree's: an arm the work added and that mutates the home passes; a class that says
    /// why it has nothing to mutate passes, as the record's schema allows; an arm the registry holds none of, an arm that
    /// mutates another file, and an arm that names neither an id nor a reason are each refused.
    /// </summary>
    [Fact]
    public async Task AClassesArm_IsJudgedAgainstTheRegistryItsWorktreeHolds_OrItsReasonForNone()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        kit.Harness.WriteConfig(kit.Main, AgentArmRegistryTests.Config);
        OrchestrationKit.Write(kit.Main, AgentArmRegistryTests.Registry, AgentArmRegistryTests.Text(AgentArmRegistryTests.Arm("alpha")));
        AgentArmRegistryTests.WriteTexts(kit.Main, "alpha");
        await kit.Harness.CommitAllAsync(kit.Main, "an arm registry", Token);
        var worktree = await kit.CreateAgentAsync("builder");

        // The work adds the arm of its class's home; the main tree's registry holds alpha alone.
        OrchestrationKit.Write(worktree, AgentArmRegistryTests.Registry, AgentArmRegistryTests.Text(AgentArmRegistryTests.Arm("alpha"), AgentArmRegistryTests.Arm("rate")));
        AgentArmRegistryTests.WriteTexts(worktree, "rate");

        JsonObject Armed(string id, JsonObject arm)
        {
            var entry = Entry(id, home: "src/rate.cpp", sites: [("src/rate.cpp", "Rate", "home")], searches: [], neighbours: [], tests: [], axes: ["format"], claim: "The rate holds over every format");
            entry["arm"] = arm;
            return entry;
        }

        var result = await Closure(kit, Weighing("src/rate.cpp")).RunAsync(
            Run(
                new WorkflowStep { Name = "class-closure", Type = "class-closure", In = "item-agent", Base = "HEAD" },
                kit.Main,
                read: new Dictionary<string, FlowValue>
                {
                    ["classes"] = FlowValue.Of(new JsonArray()),
                    ["record"] = FlowValue.Of(new JsonArray(
                        Armed("N1", new JsonObject { ["id"] = "rate", ["site"] = "src/rate.cpp" }),
                        Armed("N2", new JsonObject { ["why"] = "the class decides a name, and a name has nothing to mutate" }),
                        Armed("N3", new JsonObject { ["id"] = "ghost", ["site"] = "src/rate.cpp" }),
                        Armed("N4", new JsonObject { ["id"] = "alpha", ["site"] = "src/alpha.cpp" }),
                        Armed("N5", new JsonObject { ["why"] = " " }))),
                },
                worksIn: "builder",
                instance: "rounds#1.items[0].class-closure",
                run: OrchestrationKit.Orchestrator),
            Token);

        Assert.Equal(
            [
                "class 'N3': the arm 'ghost' is not an arm of the registry its worktree holds",
                "class 'N4': the arm 'alpha' mutates 'src/alpha.cpp', not the home 'src/rate.cpp'",
                "class 'N5': arms nothing and says not why, but this repository declares an arms registry",
            ],
            [.. result.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);
    }

    /// <summary>
    /// A class-closure step's view of the run's codebase graph is synced where it starts, as a reason step's is, so what
    /// the worktree still declares is read from the tree as the work left it, never from a view synced before the
    /// builder's last edits: a site whose declaration the work took out - its name left in a comment, which text alone
    /// would still find - is deleted, by the view.
    /// </summary>
    [Fact]
    public async Task AClassClosureStepsView_IsSyncedWhereItStarts_AndDeclarationsAreReadFromIt()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "src/Old.cs", "namespace Rates;\n\npublic class Old\n{\n    public int Rate() => 1;\n}\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the site", Token);
        var worktree = await kit.CreateAgentAsync("builder");
        await kit.Harness.CodebaseGraph.SyncAsync(FlowGraphSyncs.RequestOf(await kit.Harness.ContextLoader.LoadAsync(kit.Main, Token), new FlowRunPlace(kit.Main, OrchestrationKit.Orchestrator, null, Flow), "test"), Token);

        var runners = new FlowStepRunners([Closure(kit, Weighing("src/Old.cs"), kit.Harness.CodebaseGraph)], kit.Harness.GraphSyncs);
        var run = ClosureRun(
            kit,
            Class("c-rate", home: "src/Home.cs", symbol: "Home"),
            Entry("c-rate", home: "src/Home.cs", sites: [("src/Old.cs", "Old", "deleted")], searches: [], neighbours: [], tests: [], axes: ["format"], claim: "The rate holds over every format"));

        // An earlier step synced the agent's view, which holds the class the work has not taken out yet.
        await kit.Harness.GraphSyncs.AtStartAsync(run, Token);
        var before = await runners.RunAsync(run, Token);

        Assert.Equal(
            ["class 'c-rate': the site 'src/Old.cs' is deleted, but it still declares 'Old'"],
            [.. before.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);

        // The work takes the class out; the view, synced again where the next check starts, declares it no longer - read
        // as it was synced before, it would.
        OrchestrationKit.Write(worktree, "src/Old.cs", "namespace Rates;\n\n// Old moved to Home.\npublic class Other\n{\n}\n");

        var after = await runners.RunAsync(run, Token);

        Assert.Empty(after.Output!["problems"]!.AsArray());
        Assert.True(after.Passed);
    }

    /// <summary>
    /// A site's symbol is read by the one owner of a key path's form, as the queries and the context pack read a name: a
    /// site that is a data key whose own name holds a dot - the ASP.NET Core template's logging level, its name quoted in
    /// brackets in the file - and marked deleted is still declared where the file holds the key, and said so, written by
    /// its whole path, by the quoted place alone, or with single quotes; and a class's member is still found written with
    /// its scopes, as it was. Read by a suffix after a dot alone, the key written by its quoted place or with single quotes
    /// was found by nothing: a quoted place ends with a bracket, and the file's text holds none.
    /// </summary>
    [Fact]
    public async Task ADeletedSitesSymbol_IsReadByTheOneOwnerOfAKeyPathsForm()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "config/appsettings.json", "{\n  \"Logging\": {\n    \"LogLevel\": {\n      \"Microsoft.AspNetCore\": \"Warning\"\n    }\n  }\n}\n");
        OrchestrationKit.Write(kit.Main, "src/Old.cs", "namespace Rates;\n\npublic class Old\n{\n    public int Rate() => 1;\n}\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the sites", Token);
        await kit.CreateAgentAsync("builder");
        await kit.Harness.CodebaseGraph.SyncAsync(FlowGraphSyncs.RequestOf(await kit.Harness.ContextLoader.LoadAsync(kit.Main, Token), new FlowRunPlace(kit.Main, OrchestrationKit.Orchestrator, null, Flow), "test"), Token);

        var runners = new FlowStepRunners([Closure(kit, Weighing("config/appsettings.json", "src/Old.cs"), kit.Harness.CodebaseGraph)], kit.Harness.GraphSyncs);

        (string Path, string Symbol)[] sites =
        [
            ("config/appsettings.json", "Logging.LogLevel[\"Microsoft.AspNetCore\"]"),
            ("config/appsettings.json", "[\"Microsoft.AspNetCore\"]"),
            ("config/appsettings.json", "Logging.LogLevel['Microsoft.AspNetCore']"),
            ("src/Old.cs", "Old.Rate"),
        ];

        foreach (var (path, symbol) in sites)
        {
            var run = ClosureRun(
                kit,
                Class("c-rate", home: "src/Home.cs", symbol: "Home"),
                Entry("c-rate", home: "src/Home.cs", sites: [(path, symbol, "deleted")], searches: [], neighbours: [], tests: [], axes: ["format"], claim: "The rate holds over every format"));

            await kit.Harness.GraphSyncs.AtStartAsync(run, Token);
            var result = await runners.RunAsync(run, Token);

            Assert.Equal(
                [$"class 'c-rate': the site '{path}' is deleted, but it still declares '{symbol}'"],
                [.. result.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);
        }
    }

    /// <summary>
    /// A planned class holds the record to the plan: the record's universe holds every axis the plan names, and the claim
    /// names each; a home other than the planned one is refused unless the record says why it moved; and a deleted site
    /// that names no symbol of its own is judged, and named, by its home's.
    /// </summary>
    [Fact]
    public void APlannedClass_HoldsTheRecordToItsAxesAndItsHome()
    {
        var planned = Class("c-rate", home: "src/Home.cs", symbol: "Home");
        ((JsonArray)planned["universe"]!["axes"]!).Add(new JsonObject { ["name"] = "direction", ["values"] = new JsonArray("in", "out"), ["declaredIn"] = "src/Home.cs" });
        var moved = Entry("c-rate", home: "src/Rate.cs", sites: [("src/Old.cs", "", "deleted")], searches: [], neighbours: [], tests: [], axes: ["format"], claim: "The rate holds over every format");
        var proof = RunProof(changed: [], calls: [], declares: (path, symbol) => path == "src/Old.cs" && symbol == "Home");

        var judged = ClassClosureRules.Judge([PlannedClass.In(planned)!], [ClassClosureRecord.In(moved)!], proof);

        Assert.Equal(
            [
                "class 'c-rate': the home is 'Home' in 'src/Rate.cs', not the planned 'Home' in 'src/Home.cs', and the record says not why it moved (homeMoved)",
                "class 'c-rate': the site 'src/Old.cs' is deleted, but it still declares 'Home'",
                "class 'c-rate': the universe leaves out the planned axis 'direction'",
                "class 'c-rate': the claim does not name the axis 'direction'",
            ],
            judged.Problems);

        moved["homeMoved"] = new JsonObject { ["why"] = "the rate is decided where the rates are read" };
        moved["universe"]!["axes"]!.AsArray().Add(new JsonObject { ["name"] = "direction", ["values"] = new JsonArray("in", "out") });
        moved["claim"] = "The rate holds over every format and direction";

        Assert.Equal(
            ["class 'c-rate': the site 'src/Old.cs' is deleted, but it still declares 'Home'"],
            ClassClosureRules.Judge([PlannedClass.In(planned)!], [ClassClosureRecord.In(moved)!], proof).Problems);
    }

    /// <summary>
    /// A class's paths are read as the plan check that took them reads them - either separator - and compared as this
    /// machine compares paths, so a path the plan check took is never another file here for its spelling: a plan that
    /// writes the home and the site with backslashes and in another case than git does names the record's home and site
    /// where the machine does not tell case apart, and a record that copies the plan's spelling names the paths git gives
    /// the work - the paths the agent changed, the arm's site and the home's touches. Compared by their every character, a
    /// machine that tells case apart, the separators still read as one and the spellings in another case name other files.
    /// </summary>
    [Fact]
    public void AClassesPaths_AreReadAsThePlanCheckReadsThem_AndComparedAsThisMachineComparesPaths()
    {
        var planned = PlannedClass.In(Class("c-rate", home: "src\\rates\\home.cs", symbol: "Home", site: "src\\rates\\old.cs"))!;
        var gits = Record("src/Rates/Home.cs", "src/Rates/Old.cs");
        var plans = Record("src\\rates\\home.cs", "src\\rates\\old.cs");

        // A machine that does not tell case apart: each spelling is the one file, and the home's touches by other work ask
        // its audit whichever spelling the record keeps.
        foreach (var record in new[] { gits, plans })
        {
            var judged = ClassClosureRules.Judge([planned], [record], Proof(StringComparison.OrdinalIgnoreCase));

            Assert.Empty(judged.Problems);
            Assert.Equal(["class 'c-rate': home touched 2 times in this run by other work"], judged.Audits);
            Assert.Equal([new ClassClosureTouch("c-rate", record.HomePath, 2, 3)], judged.Touches);
        }

        Assert.Equal(("src/rates/home.cs", "src/rates/old.cs"), (plans.HomePath, plans.Sites.Single().Path));

        // A machine that tells case apart: git's spelling is not the plan's, and the plan's is not the paths git gave the
        // work, whose touches it then has none of.
        var byCase = ClassClosureRules.Judge([planned], [gits], Proof(StringComparison.Ordinal));

        Assert.Equal(
            [
                "class 'c-rate': the home is 'Home' in 'src/Rates/Home.cs', not the planned 'Home' in 'src/rates/home.cs', and the record says not why it moved (homeMoved)",
                "class 'c-rate': the site 'src/rates/old.cs' has no after in the record",
            ],
            byCase.Problems);

        var copied = ClassClosureRules.Judge([planned], [plans], Proof(StringComparison.Ordinal));

        Assert.Equal(
            [
                "class 'c-rate': the site 'src/rates/old.cs' reads its home, but the agent changed nothing at it",
                "class 'c-rate': the arm 'rate' mutates 'src/Rates/Home.cs', not the home 'src/rates/home.cs'",
            ],
            copied.Problems);
        Assert.Empty(copied.Audits);
        Assert.Equal([new ClassClosureTouch("c-rate", "src/rates/home.cs", 0, 0)], copied.Touches);

        static ClassClosureRecord Record(string home, string site)
        {
            var entry = Entry("c-rate", home, sites: [(site, "Old", "reads-home")], searches: [], neighbours: [], tests: [], axes: ["format"], claim: "The rate holds over every format");
            entry["arm"] = new JsonObject { ["id"] = "rate" };

            return ClassClosureRecord.In(entry)!;
        }

        // The paths git gives the work: what the agent changed, the registry's arm and the home's touches, each spelt as git
        // spells the files.
        static ClassClosureProof Proof(StringComparison comparison)
            => new(
                Changed: ["src/Rates/Old.cs"],
                Calls: new Dictionary<string, DssHarness.Core.Agents.AgentStreamCall>(),
                Measures: new Dictionary<string, ClassClosureMeasure>(),
                Declares: null,
                TestDeclared: null,
                Arms: [("rate", "src/Rates/Home.cs")],
                TouchesByOthers: new Dictionary<string, int> { ["src/Rates/Home.cs"] = 2 },
                TouchesAll: new Dictionary<string, int> { ["src/Rates/Home.cs"] = 3 },
                TouchesRequired: 1,
                HotspotAt: 5,
                ReopenedTwice: null,
                comparison);
    }

    /// <summary>
    /// A run's proof reads none of what it read as given: no change at a site that reads its home is a problem, and a call
    /// its streams do not hold is not made, the streams holding none at all. A manual lane's proof reads them as given, its
    /// command lines - but a search cites a call, a measurement names one, and its record names a class: an empty record
    /// closes no row.
    /// </summary>
    [Fact]
    public void ARunsProof_ReadsNothingAsGiven_AndAManualLanesRecordStillCitesItsCalls_AndNamesAClass()
    {
        var entry = ClassClosureRecord.In(Entry(
            "c-rate",
            home: "src/Home.cs",
            sites: [("src/A.cs", "Read", "reads-home")],
            searches: [("the rate", "rate", "made-up"), ("the readers", "readers", "")],
            neighbours: [("rate:x", "made-up", "passed", ""), ("rate:y", "", "passed", "")],
            tests: [],
            axes: ["format"],
            claim: "The rate holds over every format"))!;

        Assert.Equal(
            [
                "class 'c-rate': the site 'src/A.cs' reads its home, but the agent changed nothing at it",
                "class 'c-rate': the search for 'the rate' cites the call 'made-up', which is not a tool call of this item's stored streams",
                "class 'c-rate': the search for 'the readers' cites no call: a search that was not made does not count",
                "class 'c-rate': the neighbour 'rate:x' is measured by the call 'made-up', which is not a measure call of this item's stored streams",
                "class 'c-rate': the neighbour 'rate:y' is measured by no call",
            ],
            ClassClosureRules.Judge([], [entry], RunProof(changed: [], calls: [])).Problems);

        var manual = ClassClosureProof.Manual(arms: null, StringComparison.Ordinal);

        Assert.True(manual.ByHand);
        Assert.Equal(
            [
                "class 'c-rate': the search for 'the readers' cites no call: a search that was not made does not count",
                "class 'c-rate': the neighbour 'rate:y' is measured by no call",
            ],
            ClassClosureRules.Judge([], [entry], manual).Problems);
        Assert.Equal(
            ["the record names no class: a row closes by the record of each class it closes, and an empty one closes none"],
            ClassClosureRules.Judge([], [], manual).Problems);

        // A run whose item decides no class and whose builder found none answers an empty record, and it passes.
        Assert.True(ClassClosureRules.Judge([], [], RunProof(changed: [], calls: [])).Passed);
    }

    /// <summary>
    /// The arms the builder's answer lists for the item's own sweep are the arms its record names, each class's own, and
    /// no other: an arm the record names that the list leaves out would never be swept, and one listed that no class arms
    /// would be swept for nothing the record claims - both named in one problem. An arm is one id however its case is
    /// written, as the registry keeps them; the order is the answer's own; and a record that arms nothing lists nothing.
    /// </summary>
    [Fact]
    public void TheArmsTheAnswerListsForTheItemsSweep_AreTheArmsItsRecordNames()
    {
        ClassClosureRecord Armed(string id, string? arm)
        {
            var entry = Entry(id, home: "src/Home.cs", sites: [("src/Home.cs", "Home", "home")], searches: [], neighbours: [], tests: [], axes: ["format"], claim: "The rate holds over every format");

            if (arm is not null)
            {
                entry["arm"] = new JsonObject { ["id"] = arm };
            }

            return ClassClosureRecord.In(entry)!;
        }

        IReadOnlyList<ClassClosureRecord> record = [Armed("c-rate", "RATE-1"), Armed("c-tax", "TAX-1"), Armed("c-none", null)];

        IReadOnlyList<string> Problems(IReadOnlyList<ClassClosureRecord> judged, IReadOnlyList<string>? listed)
            => ClassClosureRules.Judge([], judged, ClassClosureProof.Manual(arms: null, StringComparison.Ordinal) with { ListedArms = listed }).Problems;

        Assert.Empty(Problems(record, ["TAX-1", "RATE-1"]));
        Assert.Empty(Problems(record, ["rate-1", "Tax-1", "RATE-1"]));
        Assert.Empty(Problems([Armed("c-none", null)], []));
        Assert.Empty(Problems(record, null));
        Assert.Equal(
            ["the arms the answer lists for the item's own sweep are not those its record names: 'TAX-1' armed by the record and not listed; 'FEE-1', 'OLD-2' listed and armed by no class of the record - list each arm the record names, and no other"],
            Problems(record, ["RATE-1", "FEE-1", "OLD-2"]));
        Assert.Equal(
            ["the arms the answer lists for the item's own sweep are not those its record names: 'RATE-1', 'TAX-1' armed by the record and not listed - list each arm the record names, and no other"],
            Problems(record, []));
        Assert.Equal(
            ["the arms the answer lists for the item's own sweep are not those its record names: 'RATE-1' listed and armed by no class of the record - list each arm the record names, and no other"],
            Problems([Armed("c-none", null)], ["RATE-1"]));
    }

    /// <summary>
    /// A neighbour measured by a call is held to what the call's own record says it measured, never to the closing record's
    /// word for it: a call whose record cannot be read is refused, naming where the record is; a call that measured another
    /// cell says nothing of this one; and a call that answered otherwise than the closing record says - failed or
    /// unmeasured where it says passed, or passed where it says a failure was fixed - is refused, naming both. A neighbour
    /// whose call measured its cell and answered its result passes, failed-fixed standing on a call that answered failed:
    /// the measurement showed the failure, and the fix is the item's, shown by a later call of the cell that passed. A
    /// manual lane, whose measurements no stream holds, reads them as given.
    /// </summary>
    [Fact]
    public void ANeighbour_IsHeldToWhatItsMeasureCallsOwnRecordSaysItMeasured()
    {
        var written = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);
        var entry = ClassClosureRecord.In(Entry(
            "c-rate",
            home: "src/Home.cs",
            sites: [("src/Home.cs", "Home", "home")],
            searches: [],
            neighbours:
            [
                ("rate:a", "m-unread", "passed", ""),
                ("rate:b", "m-other", "passed", ""),
                ("rate:c", "m-failed", "passed", ""),
                ("rate:d", "m-unmeasured", "passed", ""),
                ("rate:e", "m-passed-e", "failed-fixed", ""),
                ("rate:f", "m-passed-f", "passed", ""),
                ("rate:g", "m-failed-g", "failed-fixed", ""),
            ],
            tests: [],
            axes: ["format"],
            claim: "The rate holds over every format"))!;
        var unread = Path.Combine("steps", "impl-1", "measures", "m-unread", "measure.json");
        var measures = new Dictionary<string, ClassClosureMeasure>(StringComparer.Ordinal)
        {
            ["m-unread"] = new(unread, null),
            ["m-other"] = new("m-other.json", On("rate:z", "passed", written)),
            ["m-failed"] = new("m-failed.json", On("rate:c", "failed", written)),
            ["m-unmeasured"] = new("m-unmeasured.json", On("rate:d", "unmeasured", written)),
            ["m-passed-e"] = new("m-passed-e.json", On("rate:e", "passed", written)),
            ["m-passed-f"] = new("m-passed-f.json", On("rate:f", "passed", written)),
            ["m-failed-g"] = new("m-failed-g.json", On("rate:g", "failed", written)),
            ["m-passed-g"] = new("m-passed-g.json", On("rate:g", "passed", written.AddMinutes(1))),
        };

        Assert.Equal(
            [
                $"class 'c-rate': the neighbour 'rate:a' is measured by the call 'm-unread', whose record '{unread}' cannot be read",
                "class 'c-rate': the neighbour 'rate:b' is measured by the call 'm-other', which measured the cell 'rate:z'",
                "class 'c-rate': the neighbour 'rate:c' is 'passed' in the record, but the call 'm-failed' that measured it answered 'failed'",
                "class 'c-rate': the neighbour 'rate:d' is 'passed' in the record, but the call 'm-unmeasured' that measured it answered 'unmeasured'",
                "class 'c-rate': the neighbour 'rate:e' is 'failed-fixed' in the record, but the call 'm-passed-e' that measured it answered 'passed'",
            ],
            ClassClosureRules.Judge([], [entry], RunProof(changed: [], calls: [], measures: measures)).Problems);

        // A manual lane's measurements are its command lines, read as given.
        Assert.Empty(ClassClosureRules.Judge([], [entry], ClassClosureProof.Manual(arms: null, StringComparison.Ordinal)).Problems);

        // Each measurement as the tool's records name the calls of these: the leg 'windows', as 'debug', with the filter 'Rate'.
        static ClassClosureMeasured On(string cell, string result, DateTime at) => new("windows", "debug", "Rate", cell, result, at);
    }

    /// <summary>
    /// The check reads what each measure call of the agent's stored streams measured from the call's own record, in the
    /// directory of the step whose stream holds it - whatever the call answered, so a failed cell's call, which its stream
    /// holds as one that did not succeed, is a measure call too - and never from the stream's text of the answer: a record
    /// the call's own step does not hold is not read from another step's, a call whose stream says passed is held to the
    /// failure its record keeps, and a record that does not say the leg, variant and filter its call ran cannot be read,
    /// for what it measured cannot be told. A neighbour whose call measured its cell and answered its result passes, a
    /// failure fixed among them where the cell was measured again after it and passed.
    /// </summary>
    [Fact]
    public async Task TheClassClosureCheck_ReadsWhatEachMeasureCallMeasured_FromItsOwnRecord_InTheStepWhoseStreamHoldsIt()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("builder");
        var failed = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

        // The implementation's step measured x, passed; the rework's measured y and failed - its call answered as one that
        // did not succeed - and measured it again after its fix, passing; it measured z, its stream's text saying passed
        // where its record keeps a failure, and measured another cell than the one the record names. The record of the
        // implementation's second call is kept only in the rework's step, whose stream does not hold it; the record of the
        // rework's last call names no filter, so what it measured cannot be told.
        WriteStream(kit.Harness, kit.Layout, "builder", MeasureCalls(("toolu_1", true), ("toolu_3", true)), step: "impl-1");
        WriteStream(kit.Harness, kit.Layout, "builder", MeasureCalls(("toolu_2", false), ("toolu_4", true), ("toolu_5", true), ("toolu_6", true), ("toolu_7", true)), step: "rework-1");
        WriteMeasure(kit.Layout, "builder", "impl-1", "toolu_1", "rate:x", Measured("passed"));
        WriteMeasure(kit.Layout, "builder", "rework-1", "toolu_2", "rate:y", Measured("failed"), written: failed);
        WriteMeasure(kit.Layout, "builder", "rework-1", "toolu_3", "rate:w", Measured("passed"));
        WriteMeasure(kit.Layout, "builder", "rework-1", "toolu_4", "rate:z", Measured("failed"));
        WriteMeasure(kit.Layout, "builder", "rework-1", "toolu_5", "rate:other", Measured("passed"));
        WriteMeasure(kit.Layout, "builder", "rework-1", "toolu_6", "rate:y", Measured("passed"), written: failed.AddMinutes(1));
        var filterless = DssHarness.Core.Agents.AgentMeasureSupport.RecordFileOf(kit.Layout.AgentStepDirectory("builder", "rework-1"), "toolu_7");
        Directory.CreateDirectory(Path.GetDirectoryName(filterless)!);
        File.WriteAllText(filterless, """{"leg":"windows","variant":"debug","cell":"rate:u","result":"passed"}""" + "\n");

        var judged = await Closure(kit, Weighing("src/Home.cs")).RunAsync(
            ClosureRun(
                kit,
                Class("c-rate", home: "src/Home.cs", symbol: "Home"),
                Measuring(("rate:x", "toolu_1", "passed"), ("rate:y", "toolu_2", "failed-fixed"), ("rate:w", "toolu_3", "passed"), ("rate:z", "toolu_4", "passed"), ("rate:v", "toolu_5", "passed"), ("rate:u", "toolu_7", "passed"))),
            Token);

        Assert.Equal(
            [
                $"class 'c-rate': the neighbour 'rate:w' is measured by the call 'toolu_3', whose record '{DssHarness.Core.Agents.AgentMeasureSupport.RecordFileOf(kit.Layout.AgentStepDirectory("builder", "impl-1"), "toolu_3")}' cannot be read",
                "class 'c-rate': the neighbour 'rate:z' is 'passed' in the record, but the call 'toolu_4' that measured it answered 'failed'",
                "class 'c-rate': the neighbour 'rate:v' is measured by the call 'toolu_5', which measured the cell 'rate:other'",
                $"class 'c-rate': the neighbour 'rate:u' is measured by the call 'toolu_7', whose record '{filterless}' cannot be read",
            ],
            [.. judged.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);
        Assert.Equal((false, true, ClassClosureExit.Findings), (judged.Output["passed"]!.GetValue<bool>(), judged.Red, judged.Exit));

        // The neighbours whose calls measured their cells and answered their results pass, a fix claimed of the failure a
        // measurement showed among them, the cell measured again after it and passing.
        var passed = await Closure(kit, Weighing("src/Home.cs")).RunAsync(
            ClosureRun(kit, Class("c-rate", home: "src/Home.cs", symbol: "Home"), Measuring(("rate:x", "toolu_1", "passed"), ("rate:y", "toolu_2", "failed-fixed"))),
            Token);

        Assert.Empty(passed.Output!["problems"]!.AsArray());
        Assert.Equal((true, false, HarnessExit.Success), (passed.Output["passed"]!.GetValue<bool>(), passed.Red, passed.Exit));
    }

    /// <summary>
    /// A neighbour the record says failed and was fixed stands on the call that measured the failure, and the fix is shown
    /// only by the same measurement made again: of the measure calls of the item's stored streams - in any of its steps -
    /// that ran the same leg, as the same variant, with the same filter, naming the same cell, their records written after
    /// the failing call's, the newest to measure anything answered passed. Without one - none at all, one that measured
    /// another cell, a pass of other tests, on another leg or built otherwise under the cell's name, or one whose record was
    /// written before the failing call's - or where the newest answered failed - failing again, or fixed and broken again -
    /// the fix is the builder's word alone, and the neighbour is refused. A call answered unmeasured measured nothing of the
    /// cell and is never the newest word: a failure it follows still stands against the fix, and a fix it follows stands,
    /// as does a failure measured again and then fixed.
    /// </summary>
    [Fact]
    public async Task AFailedFixedNeighbour_StandsOnlyWhereTheNewestLaterCallOfItsMeasurementPassed()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("builder");
        var failed = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

        // The implementation measured each cell failing at once, on the leg 'windows' as 'debug' with the filter 'Rate'.
        // The rework measured a passing a minute later, never b again, another cell than c, d failing again, and e passing
        // - but a minute before its failure was measured; f passing with other tests, g on another leg and h built
        // otherwise, each under the cell's name; i passing and, a minute on, failing again; j failing again and then
        // passing; k passing and then unmeasured; l passing, failing again and then unmeasured.
        string[] cells = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l"];
        WriteStream(kit.Harness, kit.Layout, "builder", MeasureCalls([.. cells.Select(cell => ($"fail-{cell}", false))]), step: "impl-1");
        WriteStream(
            kit.Harness,
            kit.Layout,
            "builder",
            MeasureCalls(
                ("again-a", true),
                ("again-c", true),
                ("again-d", false),
                ("again-e", true),
                ("again-f", true),
                ("again-g", true),
                ("again-h", true),
                ("again-i", true),
                ("broken-i", false),
                ("again-j", false),
                ("fixed-j", true),
                ("again-k", true),
                ("skipped-k", false),
                ("again-l", true),
                ("broken-l", false),
                ("skipped-l", false)),
            step: "rework-1");

        foreach (var cell in cells)
        {
            WriteMeasure(kit.Layout, "builder", "impl-1", $"fail-{cell}", $"rate:{cell}", Measured("failed"), written: failed);
        }

        WriteMeasure(kit.Layout, "builder", "rework-1", "again-a", "rate:a", Measured("passed"), written: failed.AddMinutes(1));
        WriteMeasure(kit.Layout, "builder", "rework-1", "again-c", "rate:other", Measured("passed"), written: failed.AddMinutes(1));
        WriteMeasure(kit.Layout, "builder", "rework-1", "again-d", "rate:d", Measured("failed"), written: failed.AddMinutes(1));
        WriteMeasure(kit.Layout, "builder", "rework-1", "again-e", "rate:e", Measured("passed"), written: failed.AddMinutes(-1));
        WriteMeasure(kit.Layout, "builder", "rework-1", "again-f", "rate:f", Measured("passed"), written: failed.AddMinutes(1), filter: "Smoke");
        WriteMeasure(kit.Layout, "builder", "rework-1", "again-g", "rate:g", Measured("passed"), written: failed.AddMinutes(1), leg: "linux");
        WriteMeasure(kit.Layout, "builder", "rework-1", "again-h", "rate:h", Measured("passed"), written: failed.AddMinutes(1), variant: "release");
        WriteMeasure(kit.Layout, "builder", "rework-1", "again-i", "rate:i", Measured("passed"), written: failed.AddMinutes(1));
        WriteMeasure(kit.Layout, "builder", "rework-1", "broken-i", "rate:i", Measured("failed"), written: failed.AddMinutes(2));
        WriteMeasure(kit.Layout, "builder", "rework-1", "again-j", "rate:j", Measured("failed"), written: failed.AddMinutes(1));
        WriteMeasure(kit.Layout, "builder", "rework-1", "fixed-j", "rate:j", Measured("passed"), written: failed.AddMinutes(2));
        WriteMeasure(kit.Layout, "builder", "rework-1", "again-k", "rate:k", Measured("passed"), written: failed.AddMinutes(1));
        WriteMeasure(kit.Layout, "builder", "rework-1", "skipped-k", "rate:k", Measured("unmeasured"), written: failed.AddMinutes(2));
        WriteMeasure(kit.Layout, "builder", "rework-1", "again-l", "rate:l", Measured("passed"), written: failed.AddMinutes(1));
        WriteMeasure(kit.Layout, "builder", "rework-1", "broken-l", "rate:l", Measured("failed"), written: failed.AddMinutes(2));
        WriteMeasure(kit.Layout, "builder", "rework-1", "skipped-l", "rate:l", Measured("unmeasured"), written: failed.AddMinutes(3));

        var judged = await Closure(kit, Weighing("src/Home.cs")).RunAsync(
            ClosureRun(kit, Class("c-rate", home: "src/Home.cs", symbol: "Home"), Measuring([.. cells.Select(cell => ($"rate:{cell}", $"fail-{cell}", "failed-fixed"))])),
            Token);

        Assert.Equal(
            [
                "class 'c-rate': the neighbour 'rate:b' is 'failed-fixed', and no measure call of this item's stored streams measured it passing - on the leg 'windows', as 'debug', with the filter 'Rate' - after the call 'fail-b' answered 'failed'",
                "class 'c-rate': the neighbour 'rate:c' is 'failed-fixed', and no measure call of this item's stored streams measured it passing - on the leg 'windows', as 'debug', with the filter 'Rate' - after the call 'fail-c' answered 'failed'",
                "class 'c-rate': the neighbour 'rate:d' is 'failed-fixed', but the call 'again-d', the newest to measure it after the call 'fail-d', answered 'failed'",
                "class 'c-rate': the neighbour 'rate:e' is 'failed-fixed', and no measure call of this item's stored streams measured it passing - on the leg 'windows', as 'debug', with the filter 'Rate' - after the call 'fail-e' answered 'failed'",
                "class 'c-rate': the neighbour 'rate:f' is 'failed-fixed', and no measure call of this item's stored streams measured it passing - on the leg 'windows', as 'debug', with the filter 'Rate' - after the call 'fail-f' answered 'failed'",
                "class 'c-rate': the neighbour 'rate:g' is 'failed-fixed', and no measure call of this item's stored streams measured it passing - on the leg 'windows', as 'debug', with the filter 'Rate' - after the call 'fail-g' answered 'failed'",
                "class 'c-rate': the neighbour 'rate:h' is 'failed-fixed', and no measure call of this item's stored streams measured it passing - on the leg 'windows', as 'debug', with the filter 'Rate' - after the call 'fail-h' answered 'failed'",
                "class 'c-rate': the neighbour 'rate:i' is 'failed-fixed', but the call 'broken-i', the newest to measure it after the call 'fail-i', answered 'failed'",
                "class 'c-rate': the neighbour 'rate:l' is 'failed-fixed', but the call 'broken-l', the newest to measure it after the call 'fail-l', answered 'failed'",
            ],
            [.. judged.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);
        Assert.Equal((false, true, ClassClosureExit.Findings), (judged.Output["passed"]!.GetValue<bool>(), judged.Red, judged.Exit));

        // The fixes the newest later call of the same measurement shows passing close their neighbours on their own: one
        // passing once, one failing again before it passed, and one an unmeasured call followed.
        var fixedOnes = await Closure(kit, Weighing("src/Home.cs")).RunAsync(
            ClosureRun(kit, Class("c-rate", home: "src/Home.cs", symbol: "Home"), Measuring(("rate:a", "fail-a", "failed-fixed"), ("rate:j", "fail-j", "failed-fixed"), ("rate:k", "fail-k", "failed-fixed"))),
            Token);

        Assert.Empty(fixedOnes.Output!["problems"]!.AsArray());
        Assert.Equal((true, false, HarnessExit.Success), (fixedOnes.Output["passed"]!.GetValue<bool>(), fixedOnes.Red, fixedOnes.Exit));
    }

    /// <summary>
    /// A neighbour the record says passed stands on the call it cites only while that measurement's newest word is a pass:
    /// a later call of the same leg, variant, filter and cell that answered failed - the cell broken after it was measured -
    /// refuses it, naming that call, and so does a failure whose record was written at the same moment as a later pass's,
    /// for the pass never speaks for it, and so does a failure only a call answered unmeasured follows, for that call
    /// measured nothing. A later failure of other tests under the cell's name, a later failure measured passing again after
    /// it, and a later call answered unmeasured alone leave it standing.
    /// </summary>
    [Fact]
    public async Task APassedNeighbour_StandsOnlyWhereNoLaterCallOfItsMeasurementFailedLast()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("builder");
        var passed = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

        // The implementation measured each cell passing at once. The rework measured p failing a minute later; q failing
        // and then passing; r failing with other tests; s unmeasured; t failing and passing, both a minute later; and u
        // failing and then unmeasured.
        string[] cells = ["p", "q", "r", "s", "t", "u"];
        WriteStream(kit.Harness, kit.Layout, "builder", MeasureCalls([.. cells.Select(cell => ($"pass-{cell}", true))]), step: "impl-1");
        WriteStream(
            kit.Harness,
            kit.Layout,
            "builder",
            MeasureCalls(("broken-p", false), ("broken-q", false), ("again-q", true), ("other-r", false), ("skipped-s", false), ("broken-t", false), ("again-t", true), ("broken-u", false), ("skipped-u", false)),
            step: "rework-1");

        foreach (var cell in cells)
        {
            WriteMeasure(kit.Layout, "builder", "impl-1", $"pass-{cell}", $"rate:{cell}", Measured("passed"), written: passed);
        }

        WriteMeasure(kit.Layout, "builder", "rework-1", "broken-p", "rate:p", Measured("failed"), written: passed.AddMinutes(1));
        WriteMeasure(kit.Layout, "builder", "rework-1", "broken-q", "rate:q", Measured("failed"), written: passed.AddMinutes(1));
        WriteMeasure(kit.Layout, "builder", "rework-1", "again-q", "rate:q", Measured("passed"), written: passed.AddMinutes(2));
        WriteMeasure(kit.Layout, "builder", "rework-1", "other-r", "rate:r", Measured("failed"), written: passed.AddMinutes(1), filter: "Smoke");
        WriteMeasure(kit.Layout, "builder", "rework-1", "skipped-s", "rate:s", Measured("unmeasured"), written: passed.AddMinutes(1));
        WriteMeasure(kit.Layout, "builder", "rework-1", "broken-t", "rate:t", Measured("failed"), written: passed.AddMinutes(1));
        WriteMeasure(kit.Layout, "builder", "rework-1", "again-t", "rate:t", Measured("passed"), written: passed.AddMinutes(1));
        WriteMeasure(kit.Layout, "builder", "rework-1", "broken-u", "rate:u", Measured("failed"), written: passed.AddMinutes(1));
        WriteMeasure(kit.Layout, "builder", "rework-1", "skipped-u", "rate:u", Measured("unmeasured"), written: passed.AddMinutes(2));

        var judged = await Closure(kit, Weighing("src/Home.cs")).RunAsync(
            ClosureRun(kit, Class("c-rate", home: "src/Home.cs", symbol: "Home"), Measuring([.. cells.Select(cell => ($"rate:{cell}", $"pass-{cell}", "passed"))])),
            Token);

        Assert.Equal(
            [
                "class 'c-rate': the neighbour 'rate:p' is 'passed' in the record, but the call 'broken-p', the newest to measure it after the call 'pass-p', answered 'failed'",
                "class 'c-rate': the neighbour 'rate:t' is 'passed' in the record, but the call 'broken-t', the newest to measure it after the call 'pass-t', answered 'failed'",
                "class 'c-rate': the neighbour 'rate:u' is 'passed' in the record, but the call 'broken-u', the newest to measure it after the call 'pass-u', answered 'failed'",
            ],
            [.. judged.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);
        Assert.Equal((false, true, ClassClosureExit.Findings), (judged.Output["passed"]!.GetValue<bool>(), judged.Red, judged.Exit));

        // The passes no later failure of their own measurement had the last word on close their neighbours on their own.
        var standing = await Closure(kit, Weighing("src/Home.cs")).RunAsync(
            ClosureRun(kit, Class("c-rate", home: "src/Home.cs", symbol: "Home"), Measuring(("rate:q", "pass-q", "passed"), ("rate:r", "pass-r", "passed"), ("rate:s", "pass-s", "passed"))),
            Token);

        Assert.Empty(standing.Output!["problems"]!.AsArray());
        Assert.Equal((true, false, HarnessExit.Success), (standing.Output["passed"]!.GetValue<bool>(), standing.Red, standing.Exit));
    }

    /// <summary>
    /// A search is made by a call whose tool ran, however its program exited: grep, rg and git grep exit 1 finding nothing,
    /// a call that did not succeed and the very evidence a class of one site rests on, so a search citing it stands. A call
    /// that never ran - refused, or its input unread - searched nothing, and a search citing it is refused as one; so is a
    /// call that did not succeed in a stream written before its client said whether it ran, read by whether it went through.
    /// </summary>
    [Fact]
    public async Task ASearch_IsMadeByACallThatRan_HoweverItsProgramExited()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("builder");

        WriteStream(
            kit.Harness,
            kit.Layout,
            "builder",
            [
                new DssHarness.Core.Agents.ToolCallStarted("toolu_7", "run_command", DssHarness.Core.Agents.AgentToolRunner.Harness),
                new DssHarness.Core.Agents.ToolCallFinished("toolu_7", Ok: false, TimeSpan.Zero, """{"program":"rg","args":["-n","RateOf","src"]}""", "exit 1, after 0.1 s; it printed nothing", Ran: true),
                new DssHarness.Core.Agents.ToolCallStarted("toolu_8", "Grep", DssHarness.Core.Agents.AgentToolRunner.ClaudeCode),
                new DssHarness.Core.Agents.ToolCallFinished("toolu_8", Ok: false, TimeSpan.Zero, """{"pattern":"RateOf"}""", "denied by the policy", Ran: false),
                new DssHarness.Core.Agents.ToolCallStarted("toolu_9", "run_command", DssHarness.Core.Agents.AgentToolRunner.Harness),
                new DssHarness.Core.Agents.ToolCallFinished("toolu_9", Ok: false, TimeSpan.Zero, """{"program":"rg","args":["RateOf"]}""", "exit 1"),
            ]);

        var judged = await Closure(kit, Weighing("src/Home.cs")).RunAsync(
            ClosureRun(kit, Class("c-rate", home: "src/Home.cs", symbol: "Home"), Searching(("the callers", "RateOf", "toolu_7"), ("the readers", "RateOf", "toolu_8"), ("the writers", "RateOf", "toolu_9"))),
            Token);

        Assert.Equal(
            [
                "class 'c-rate': the search for 'the readers' cites the call 'toolu_8', which never ran: a call refused, or whose input did not read, searched nothing",
                "class 'c-rate': the search for 'the writers' cites the call 'toolu_9', which never ran: a call refused, or whose input did not read, searched nothing",
            ],
            [.. judged.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);

        // The search that found nothing closes the class on its own.
        var found = await Closure(kit, Weighing("src/Home.cs")).RunAsync(
            ClosureRun(kit, Class("c-rate", home: "src/Home.cs", symbol: "Home"), Searching(("the callers", "RateOf", "toolu_7"))),
            Token);

        Assert.Empty(found.Output!["problems"]!.AsArray());
        Assert.Equal((true, false, HarnessExit.Success), (found.Output["passed"]!.GetValue<bool>(), found.Red, found.Exit));

        static JsonObject Searching(params (string Data, string Query, string Call)[] searches)
            => Entry(
                "c-rate",
                home: "src/Home.cs",
                sites: [("src/Home.cs", "Home", "home")],
                searches: searches,
                neighbours: [],
                tests: [],
                axes: ["format"],
                claim: "The rate holds over every format");
    }

    /// <summary>
    /// The events of a stored stream of measure calls, as the tool runtime's own calls record them: each started and
    /// finished under its id, finished as having gone through or not as given.
    /// </summary>
    private static IReadOnlyList<DssHarness.Core.Agents.AgentEvent> MeasureCalls(params (string Id, bool Ok)[] calls)
        => [.. calls.SelectMany(call => new DssHarness.Core.Agents.AgentEvent[]
        {
            new DssHarness.Core.Agents.ToolCallStarted(call.Id, DssHarness.Core.Agents.AgentMeasureSupport.ToolName, DssHarness.Core.Agents.AgentToolRunner.Harness),
            new DssHarness.Core.Agents.ToolCallFinished(call.Id, call.Ok, TimeSpan.Zero, """{"leg":"windows","variant":"debug","filter":"Rate","cell":"rate"}""", "passed"),
        })];

    /// <summary>One entry of the closing record for the class 'c-rate' whose neighbours are <paramref name="neighbours"/>, each measured by the call it names.</summary>
    private static JsonObject Measuring(params (string Cell, string MeasuredBy, string Result)[] neighbours)
        => Entry(
            "c-rate",
            home: "src/Home.cs",
            sites: [("src/Home.cs", "Home", "home")],
            searches: [],
            neighbours: [.. neighbours.Select(neighbour => (neighbour.Cell, neighbour.MeasuredBy, neighbour.Result, string.Empty))],
            tests: [],
            axes: ["format"],
            claim: "The rate holds over every format");

    /// <summary>
    /// A run's proof: the paths the agent changed, the calls its streams hold and what each measure call of them measured
    /// (none, where none are given), what its worktree declares, no arms registry, a home audit asked of one touch by others
    /// and a hotspot at five, its paths compared by their every character.
    /// </summary>
    private static ClassClosureProof RunProof(
        IReadOnlyList<string> changed,
        IReadOnlyList<DssHarness.Core.Agents.AgentStreamCall> calls,
        Func<string, string, bool>? declares = null,
        IReadOnlyDictionary<string, ClassClosureMeasure>? measures = null,
        IReadOnlySet<string>? reopenedTwice = null)
        => new(
            changed,
            calls.ToDictionary(call => call.Id, StringComparer.Ordinal),
            measures ?? new Dictionary<string, ClassClosureMeasure>(),
            declares,
            TestDeclared: null,
            Arms: null,
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            TouchesRequired: WorkflowStepTypes.DefaultTouches,
            HotspotAt: WorkflowStepTypes.DefaultHotspot,
            ReopenedTwice: reopenedTwice,
            StringComparison.Ordinal);

    /// <summary>An agent service whose fold's dry weighing plans to write <paramref name="written"/>, and nothing else.</summary>
    private static DssHarness.Core.Orchestration.IAgentService Weighing(params string[] written)
    {
        var agents = Substitute.For<DssHarness.Core.Orchestration.IAgentService>();
        agents.FoldAsync(Arg.Any<DssHarness.Core.Orchestration.FoldAgentRequest>(), Arg.Any<CancellationToken>())
            .Returns(DssHarness.Core.Application.UseCaseResult<DssHarness.Core.Orchestration.AgentVerbFacts>.From(
                new CommandOutcome(HarnessExit.Success, "dry run")
                {
                    Facts = new DssHarness.Core.Orchestration.AgentVerbFacts(0, "dry run", false, null, null, [], [], []) { Planned = new DssHarness.Core.Orchestration.AgentPlanned([], [], [], [], null) { Written = written } },
                }));

        return agents;
    }

    /// <summary>
    /// The <c>route-findings</c> step routes the review's findings by their class: every introduced finding goes back
    /// as itself, the reopened findings of one class go back as one send-back - the class by its own member, the rounds
    /// that reopened it and all its findings, graded by the worst of them - and a class reopened a second time goes to
    /// <c>homeAudit</c> instead, for the next action is the home audit. The item's ledger keeps, in its agent's
    /// directory, the routings that reopened each class, so a later round knows what the rounds before it reopened, and
    /// a round that reopens nothing moves nothing.
    /// </summary>
    [Fact]
    public async Task TheRouteFindingsStep_SendsAClassBackOnce_RaisesAHomeAuditOnTheSecondReopening_AndKeepsItsLedger()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("builder");

        var first = await Route(kit).RunAsync(RouteRun(kit, "rounds#1.items[0].fix#1.route",
            Routed(text: "the report ships without its frame", severity: "high"),
            Routed(text: "the rate is decided again in the report", severity: "medium", @class: "reopened", reopens: "c-rate"),
            Routed(text: "the rate is decided again in the summary", severity: "low", @class: "reopened", reopens: "c-rate")), Token);

        Assert.True(first.Passed);
        var rework = first.Output!["rework"]!.AsArray();
        Assert.Equal(2, rework.Count);
        Assert.Equal("introduced", rework[0]!["class"]!.GetValue<string>());
        Assert.Equal("the report ships without its frame", rework[0]!["text"]!.GetValue<string>());

        // A send-back names its class by a member of its own, never a finding's class, which says introduced or reopened.
        var sendBack = rework[1]!.AsObject();
        Assert.Equal(["reopenedClass", "rounds", "severity", "findings"], sendBack.Select(member => member.Key));
        Assert.Equal(("c-rate", 1, "medium"), (sendBack["reopenedClass"]!.GetValue<string>(), sendBack["rounds"]!.GetValue<int>(), sendBack["severity"]!.GetValue<string>()));
        Assert.Equal(["the rate is decided again in the report", "the rate is decided again in the summary"], sendBack["findings"]!.AsArray().Select(finding => finding!["text"]!.GetValue<string>()));
        Assert.Empty(first.Output["homeAudit"]!.AsArray());
        Assert.Equal("""[{"class":"c-rate","rounds":1}]""", first.Output["reopened"]!.ToJsonString());

        Assert.Equal(
            new FlowClosureLedgerFile(1, "builder", [new FlowClosureReopens("c-rate", ["rounds#1.items[0].fix#1.route"])]).Classes.Select(entry => (entry.Class, string.Join(",", entry.By))),
            kit.Harness.OrchestrationStore.ReadClosureLedger(kit.Layout, "builder")!.Classes.Select(entry => (entry.Class, string.Join(",", entry.By))));

        // The next round reopening the same class: the send-back is a home audit now, the count kept by the ledger.
        var second = await Route(kit).RunAsync(RouteRun(kit, "rounds#1.items[0].fix#2.route", Routed(text: "once more", @class: "reopened", reopens: "c-rate")), Token);

        Assert.Empty(second.Output!["rework"]!.AsArray());
        var audit = Assert.Single(second.Output["homeAudit"]!.AsArray());
        Assert.Equal(("c-rate", 2, "medium"), (audit!["reopenedClass"]!.GetValue<string>(), audit["rounds"]!.GetValue<int>(), audit["severity"]!.GetValue<string>()));
        Assert.Equal(["rounds#1.items[0].fix#1.route", "rounds#1.items[0].fix#2.route"], kit.Harness.OrchestrationStore.ReadClosureLedger(kit.Layout, "builder")!.Classes.Single().By);

        // A round that reopens nothing leaves the ledger where it was and routes nothing.
        var third = await Route(kit).RunAsync(RouteRun(kit, "rounds#1.items[0].fix#3.route"), Token);

        Assert.Empty(third.Output!["rework"]!.AsArray());
        Assert.Empty(third.Output["homeAudit"]!.AsArray());
        Assert.Equal("""[{"class":"c-rate","rounds":2}]""", third.Output["reopened"]!.ToJsonString());
        Assert.Equal(2, kit.Harness.OrchestrationStore.ReadClosureLedger(kit.Layout, "builder")!.Classes.Single().Reopens);
    }

    /// <summary>
    /// A routing run again - the same instance, after a crash between the ledger's write and the step's own record, a stop
    /// or a resume - counts its round once: its class is still reopened once, sent back once and never to a home audit,
    /// and the ledger is left as the first run wrote it.
    /// </summary>
    [Fact]
    public async Task ARoutingRunAgain_CountsItsRoundOnce()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("builder");
        var reopening = Routed(text: "the rate is decided again in the report", @class: "reopened", reopens: "c-rate");

        await Route(kit).RunAsync(RouteRun(kit, "rounds#1.items[0].fix#1.route", reopening), Token);
        var written = File.ReadAllText(kit.Layout.ClosureLedgerFile("builder"));

        var again = await Route(kit).RunAsync(RouteRun(kit, "rounds#1.items[0].fix#1.route", (JsonObject)reopening.DeepClone()), Token);

        Assert.True(again.Passed);
        var sendBack = Assert.Single(again.Output!["rework"]!.AsArray());
        Assert.Equal(("c-rate", 1), (sendBack!["reopenedClass"]!.GetValue<string>(), sendBack["rounds"]!.GetValue<int>()));
        Assert.Empty(again.Output["homeAudit"]!.AsArray());
        Assert.Equal(written, File.ReadAllText(kit.Layout.ClosureLedgerFile("builder")));
    }

    /// <summary>
    /// Findings the route cannot route fail the step with every problem at once, nothing routed and the ledger untouched:
    /// a finding that is no object, one with no class, a reopened one that names no class or one the item does not have -
    /// its classes' or its record's, compared exactly, case and all - and a severity off the scale, which would otherwise
    /// be graded as anything.
    /// </summary>
    [Fact]
    public async Task FindingsTheRouteCannotRoute_FailTheStep_NamingEach()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("builder");

        var bare = new JsonObject { ["aspect"] = "correctness", ["path"] = "src/one/one.cs:1", ["text"] = "wrong", ["severity"] = "high", ["production"] = true };
        var refused = await Route(kit).RunAsync(
            RouteRun(
                kit,
                "rounds#1.items[0].fix#1.route",
                [bare, Routed(@class: "reopened", reopens: ""), Routed(@class: "reopened", reopens: "C-RATE"), Routed(severity: "High"), Routed(@class: "reopened", reopens: "N1")],
                extra: JsonValue.Create("a line")),
            Token);

        Assert.Equal(HarnessExit.ConfigInvalid, refused.Exit);
        Assert.Equal(
            "'rounds#1.items[0].fix#1.route' cannot route the review's findings: "
            + "[0].class: 'none' is no class of a finding: every finding is introduced or reopened; "
            + "[1].reopens: names no class: a reopened finding names the class it reopens, exactly, and the item's are 'c-rate', 'N1'; "
            + "[2].reopens: 'C-RATE' is no class of the item: the item's are 'c-rate', 'N1'; name one exactly, or class the finding introduced; "
            + "[3].severity: 'High' is not on the scale a finding is graded on: critical, high, medium, low, nit; "
            + "[5]: is no finding: a finding is an object. Nothing was routed and the item's ledger was not changed",
            refused.Error);
        Assert.Null(kit.Harness.OrchestrationStore.ReadClosureLedger(kit.Layout, "builder"));
    }

    /// <summary>
    /// What the route refuses, a reviewer whose answer it routes is held to at its own answer: its findings, by their path
    /// in its output, so the gate asks it again; findings the route would take hold.
    /// </summary>
    [Fact]
    public void AReviewersFindings_AreHeldToWhatTheRouteTakes()
    {
        var holds = FindingRoutes.HeldTo("findings", ["c-rate"]);

        Assert.Equal(
            ["findings[0].reopens: 'c-Rate' is no class of the item: the item's are 'c-rate'; name one exactly, or class the finding introduced"],
            holds(new JsonObject { ["findings"] = new JsonArray(Routed(@class: "reopened", reopens: "c-Rate"), Routed()) }));
        Assert.Empty(holds(new JsonObject { ["findings"] = new JsonArray(Routed(@class: "reopened", reopens: "c-rate"), Routed()) }));
    }

    /// <summary>
    /// The class-closure check asks a home audit of a class the item's ledger says the reviews reopened a second time -
    /// the audit the route asked for, made by the one mechanism the check owns - and the record that carries one passes.
    /// </summary>
    [Fact]
    public async Task TheClassClosureCheck_RequiresAHomeAuditOfAClassTheLedgerReopenedTwice()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("builder");
        kit.Harness.OrchestrationStore.WriteClosureLedger(kit.Layout, "builder", new FlowClosureLedgerFile(1, "builder", [new FlowClosureReopens("c-rate", ["rounds#1.items[0].fix#1.route", "rounds#1.items[0].fix#2.route"])]));

        var entry = Entry("c-rate", home: "src/Home.cs", sites: [], searches: [], neighbours: [], tests: [], axes: ["format"], claim: "The rate holds over every format");

        var refused = await Closure(kit, Weighing()).RunAsync(ClosureRun(kit, Class("c-rate", home: "src/Home.cs", symbol: "Home"), entry), Token);

        Assert.Equal(
            ["class 'c-rate' reopened twice"],
            [.. refused.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);
        Assert.Equal(ClassClosureExit.HomeAudit, refused.Exit);

        entry["homeAudit"] = new JsonObject { ["consolidated"] = new JsonArray("the rate"), ["invariants"] = new JsonArray("one place"), ["structural"] = new JsonArray("Home holds it") };

        var audited = await Closure(kit, Weighing()).RunAsync(ClosureRun(kit, Class("c-rate", home: "src/Home.cs", symbol: "Home"), (JsonObject)entry.DeepClone()), Token);

        Assert.True(audited.Output!["passed"]!.GetValue<bool>());
    }

    /// <summary>
    /// The check reads the ledger's class ids as the route wrote them, exactly, on every machine: a class id is no path,
    /// so a class 'C-RATE' the ledger holds reopened twice is not the planned 'c-rate', whether or not this machine's file
    /// system folds case - the same records ask the same audit everywhere.
    /// </summary>
    [Fact]
    public async Task TheClassClosureCheck_ReadsTheLedgersClassIds_Exactly_OnEveryMachine()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("builder");
        kit.Harness.OrchestrationStore.WriteClosureLedger(kit.Layout, "builder", new FlowClosureLedgerFile(1, "builder", [new FlowClosureReopens("C-RATE", ["rounds#1.items[0].fix#1.route", "rounds#1.items[0].fix#2.route"])]));

        var entry = Entry("c-rate", home: "src/Home.cs", sites: [], searches: [], neighbours: [], tests: [], axes: ["format"], claim: "The rate holds over every format");

        var judged = await Closure(kit, Weighing()).RunAsync(ClosureRun(kit, Class("c-rate", home: "src/Home.cs", symbol: "Home"), entry), Token);

        Assert.Empty(judged.Output!["problems"]!.AsArray());
        Assert.True(judged.Output["passed"]!.GetValue<bool>());
    }

    /// <summary>The class-closure runner over the kit's own services, the agent service and the graph given.</summary>
    private static ClassClosureStepRunner Closure(OrchestrationKit kit, DssHarness.Core.Orchestration.IAgentService agents, DssHarness.Core.Codebase.ICodebaseGraphService? graphs = null)
        => new(
            kit.Harness.ContextLoader,
            kit.Harness.Platform,
            kit.Harness.OrchestrationStore,
            kit.Harness.FileSystem,
            agents,
            graphs ?? Substitute.For<DssHarness.Core.Codebase.ICodebaseGraphService>(),
            kit.Harness.GitClient,
            new DssHarness.Core.Mutations.ArmListingReader(SyncKit.Service(kit.Harness), kit.Harness.FileSystem, kit.Harness.Platform));

    /// <summary>A class-closure step of the kit's run, working in the agent 'builder', judging <paramref name="record"/> against the one class <paramref name="planned"/>.</summary>
    private static FlowStepRun ClosureRun(OrchestrationKit kit, JsonObject planned, params JsonObject[] record)
        => Run(
            new WorkflowStep { Name = "class-closure", Type = "class-closure", In = "item-agent", Base = "HEAD" },
            kit.Main,
            read: new Dictionary<string, FlowValue>
            {
                ["classes"] = FlowValue.Of(new JsonArray(planned)),
                ["record"] = FlowValue.Of(new JsonArray([.. record])),
            },
            worksIn: "builder",
            instance: "rounds#1.items[0].class-closure",
            run: OrchestrationKit.Orchestrator);

    /// <summary>A route-findings step of the kit's run, working in the agent 'builder', routing the findings given.</summary>
    private static RouteFindingsStepRunner Route(OrchestrationKit kit)
        => new(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore);

    /// <summary>A route-findings run of the kit's as <paramref name="instance"/>, routing <paramref name="findings"/> over the planned class 'c-rate'.</summary>
    private static FlowStepRun RouteRun(OrchestrationKit kit, string instance, params JsonObject[] findings)
        => RouteRun(kit, instance, findings, extra: null);

    /// <summary>
    /// A route-findings run of the kit's as <paramref name="instance"/>, routing <paramref name="findings"/> and
    /// <paramref name="extra"/> after them, over the planned class 'c-rate' and the class 'N1' its builder's record added.
    /// </summary>
    private static FlowStepRun RouteRun(OrchestrationKit kit, string instance, JsonObject[] findings, JsonNode? extra)
        => Run(
            new WorkflowStep { Name = "route", Type = "route-findings", In = "item-agent", Findings = "review.findings", Classes = "input.item.classes", Record = "implement.latest.closure" },
            kit.Main,
            read: new Dictionary<string, FlowValue>
            {
                ["findings"] = FlowValue.Of(new JsonArray([.. findings, .. extra is null ? Array.Empty<JsonNode>() : [extra]])),
                ["classes"] = FlowValue.Of(new JsonArray(Class("c-rate", home: "src/Home.cs", symbol: "Home"))),
                ["record"] = FlowValue.Of(new JsonArray(Entry("N1", home: "src/Other.cs", sites: [], searches: [], neighbours: [], tests: [], axes: ["format"], claim: "it holds"))),
            },
            worksIn: "builder",
            instance: instance,
            run: OrchestrationKit.Orchestrator);

    /// <summary>One finding of a review, as the review's schema holds it: classed introduced or reopened, the reopened naming its class.</summary>
    private static JsonObject Routed(string text = "wrong", string severity = "medium", string @class = "introduced", string reopens = "")
        => new()
        {
            ["aspect"] = "closure",
            ["path"] = "src/one/one.cs:1",
            ["text"] = text,
            ["severity"] = severity,
            ["production"] = true,
            ["class"] = @class,
            ["reopens"] = reopens,
        };

    /// <summary>One planned class of the item's plan, as the plan's own check reads it.</summary>
    private static JsonObject Class(string id, string home, string symbol, string? site = null)
    {
        var sites = new JsonArray();

        if (site is { })
        {
            sites.Add(new JsonObject { ["path"] = site, ["symbol"] = "Old", ["decides"] = "the same", ["then"] = "deleted" });
        }

        return new JsonObject
        {
            ["id"] = id,
            ["invariant"] = "the rate is decided in one place",
            ["home"] = new JsonObject { ["path"] = home, ["symbol"] = symbol, ["exists"] = true },
            ["sites"] = sites,
            ["searches"] = new JsonArray(new JsonObject { ["data"] = "the turnover", ["via"] = "graph", ["query"] = "turnover:", ["hits"] = 3 }),
            ["universe"] = new JsonObject { ["axes"] = new JsonArray(new JsonObject { ["name"] = "format", ["values"] = new JsonArray("a", "b"), ["declaredIn"] = "src/Home.cs" }), ["cells"] = 2 },
            ["action"] = "fix-at-home",
            ["notCovered"] = "none",
        };
    }

    /// <summary>One entry of the builder's closing record, as the implement schema holds it.</summary>
    private static JsonObject Entry(
        string id,
        string home,
        (string Path, string Symbol, string After)[] sites,
        (string Data, string Query, string Call)[] searches,
        (string Cell, string MeasuredBy, string Result, string Reason)[] neighbours,
        string[] tests,
        string[] axes,
        string claim)
    {
        var entry = new JsonObject
        {
            ["class"] = id,
            ["invariant"] = "the rate is decided in one place",
            ["home"] = new JsonObject { ["path"] = home, ["symbol"] = "Home" },
            ["sites"] = new JsonArray(sites.Select(site => (JsonNode?)new JsonObject { ["path"] = site.Path, ["symbol"] = site.Symbol, ["before"] = "each file", ["after"] = site.After }).ToArray()),
            ["searches"] = new JsonArray(searches.Select(search => (JsonNode?)new JsonObject { ["data"] = search.Data, ["via"] = "graph", ["query"] = search.Query, ["call"] = search.Call }).ToArray()),
            ["universe"] = new JsonObject
            {
                ["axes"] = new JsonArray(axes.Select(axis => (JsonNode?)new JsonObject { ["name"] = axis, ["values"] = new JsonArray("a") }).ToArray()),
                ["cellsPinned"] = 1,
                ["cellsArmed"] = 0,
            },
            ["pin"] = new JsonObject { ["tests"] = new JsonArray(tests.Select(test => (JsonNode?)test).ToArray()), ["tableDriven"] = true, ["over"] = "the universe" },
            ["arm"] = new JsonObject { ["why"] = "the repository keeps no arms" },
            ["neighbours"] = new JsonArray(neighbours.Select(neighbour => (JsonNode?)new JsonObject
            {
                ["cell"] = neighbour.Cell,
                ["direction"] = "the same",
                ["form"] = "the same source",
                ["reference"] = "the leg",
                ["measuredBy"] = neighbour.MeasuredBy,
                ["result"] = neighbour.Result,
                ["reason"] = neighbour.Reason,
            }).ToArray()),
            ["claim"] = claim,
        };

        return entry;
    }

    /// <summary>Writes the stored stream of the agent's step <paramref name="step"/> under its own directory, one event a line.</summary>
    private static void WriteStream(HarnessFactory harness, OrchestratorLayout layout, string agent, IReadOnlyList<DssHarness.Core.Agents.AgentEvent> events, string step = "impl-1")
    {
        Directory.CreateDirectory(layout.AgentStepDirectory(agent, step));

        var lines = events.Select(@event => System.Text.Json.JsonSerializer.SerializeToNode(@event, typeof(DssHarness.Core.Agents.AgentEvent), DssHarness.Core.FileSystem.JsonStateFile.LineOptions)!.ToJsonString(DssHarness.Core.FileSystem.JsonStateFile.LineOptions));

        File.WriteAllLines(Path.Combine(layout.AgentStepDirectory(agent, step), DssHarness.Core.Agents.StoredStream.FileName), lines);
    }

    /// <summary>
    /// Keeps what the measure call <paramref name="id"/> of the agent's step <paramref name="step"/> answered as the tool
    /// runtime keeps it: the call said back with its answer (<see cref="DssHarness.Core.Agents.AgentMeasureSupport.Said"/>)
    /// in the call's own record (<see cref="DssHarness.Core.Agents.AgentMeasureSupport.RecordFileOf"/>), dated
    /// <paramref name="written"/> where it is given - when the runtime answered the call - and now where it is not. The
    /// call ran the leg 'windows' as 'debug' with the filter 'Rate' unless it names its own.
    /// </summary>
    private static void WriteMeasure(
        OrchestratorLayout layout,
        string agent,
        string step,
        string id,
        string cell,
        DssHarness.Core.Agents.AgentMeasureAnswer answer,
        DateTime? written = null,
        string leg = "windows",
        string variant = "debug",
        string filter = "Rate")
    {
        var directory = layout.AgentStepDirectory(agent, step);
        var call = new DssHarness.Core.Agents.AgentMeasureCall(id, leg, variant, filter, cell, DssHarness.Core.Agents.AgentMeasureSupport.RecordsOf(directory, id));
        var record = DssHarness.Core.Agents.AgentMeasureSupport.RecordFileOf(directory, id);

        Directory.CreateDirectory(call.Records);
        File.WriteAllText(record, DssHarness.Core.Agents.AgentMeasureSupport.Said(call, answer).ToJsonString() + "\n");

        if (written is { } dated)
        {
            File.SetLastWriteTimeUtc(record, dated);
        }
    }

    /// <summary>What a measure call answers that showed <paramref name="result"/>: passed, failed or unmeasured.</summary>
    private static DssHarness.Core.Agents.AgentMeasureAnswer Measured(string result)
        => result switch
        {
            "passed" => new(Measured: true, Passed: true, "passed", null, null),
            "failed" => new(Measured: true, Passed: false, "failed", "the tests failed", null),
            _ => new(Measured: false, Passed: false, string.Empty, null, "the leg was skipped"),
        };

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

    private static FlowStepRun Run(WorkflowStep step, string repository, IReadOnlyDictionary<string, FlowValue>? read = null, IReadOnlyList<FlowAnswerGiven>? answers = null, string? worksIn = null, string? instance = null, string run = "r1")
        => new(step, instance ?? step.Name, 1, FlowValue.Missing, null, null, answers ?? [], new NoEvents(), Place: new FlowRunPlace(repository, run, null, Flow), Read: read, WorksIn: worksIn);

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

        public void Used(FlowTurnSpend turn) => throw new InvalidOperationException("a check spends nothing");

        public void Held(IReadOnlyList<string> on) => throw new InvalidOperationException("a check places no leg");

        public void HandoffTaken() => throw new InvalidOperationException("a check takes no handoff");
    }
}
