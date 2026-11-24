using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Isolation;
using DssHarness.Core.Application;
using DssHarness.Core.Application.Legs;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.Schemas;

namespace DssHarness.Tests;

/// <summary>
/// The leg steps of slice 20b - build, test, run and check-mutations - each a thin adapter over its command's use case:
/// where each leg works told to the engine as the leg service says it, each leg's verdict read on its own, the red legs
/// answered alone with the phase that did not pass, a leg run something else ended ending the step with its own code,
/// and the ledger - with each red leg's failing log, in an agent's worktree - kept in the step's directory.
/// </summary>
public sealed class LegStepRunnerTests : IDisposable
{
    /// <summary>Every directory a test made, removed once it ends.</summary>
    private readonly List<TempDirectory> _made = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var made in _made)
        {
            made.Dispose();
        }
    }

    private static readonly WorkflowDefinition Flow = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
        name: flow
        steps:
          - name: orient
            type: orient
        """);

    /// <summary>A repository with the harness initialised by <paramref name="config"/>, committed, on branch 'work'.</summary>
    private async Task<(HarnessFactory Harness, TempDirectory Repository)> RepositoryAsync(HarnessConfig config)
    {
        var harness = new HarnessFactory();
        var repository = new TempDirectory();
        _made.Add(repository);

        repository.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(repository.Path, Token, config);
        await harness.CommitAllAsync(repository.Path, "initial", Token);
        await harness.RunGitAsync(repository.Path, ["checkout", "--quiet", "-b", "work"], Token);

        return (harness, repository);
    }

    /// <summary>
    /// One leg this machine runs - in a copy of its own where <paramref name="localCopy"/>, under no limit on its paths -
    /// a runner whose one phase passes, and the <paramref name="workflow"/> declared, where one is named.
    /// </summary>
    private static HarnessConfig Config(bool localCopy, string? workflow = null, string? isolation = null)
    {
        var platform = new HostPlatform();

        return new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", LocalCopy = localCopy } },
            PredefinedRunners =
            {
                ["probe"] = new RunnerConfig { Phases = [new RunnerPhase { Name = "look", Command = ["dotnet", "--version"] }] },
            },
            Workflows = workflow is null ? null : [workflow],
            Worktrees = new WorktreeSettings { PathLimit = 100_000 },
            AgentPolicy = isolation is null ? null : new AgentPolicyConfig { Isolation = isolation },
        };
    }

    private static FlowStepRun Run(WorkflowStep step, string repository, IFlowRunEvents? events = null, string? worksIn = null, IReadOnlyDictionary<string, FlowValue>? read = null, string? instance = null)
        => new(step, instance ?? step.Name, 1, FlowValue.Missing, null, null, [], events ?? new Said(), Place: new FlowRunPlace(repository, "r1", null, Flow), Read: read, WorksIn: worksIn);

    /// <summary>Every event a step's runner said, in the words a test reads them in.</summary>
    private sealed class Said : IFlowRunEvents
    {
        private readonly List<string> _said = [];

        /// <summary>What was said, in order.</summary>
        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_said)
                {
                    return [.. _said];
                }
            }
        }

        public void LegPlaced(string leg, bool inACopy) => Add($"placed {leg} in {(inACopy ? "a copy" : "the tree")}");

        public void EveryLegPlaced() => Add("every leg placed");

        public void LegCopyMade(string leg) => Add($"copy of {leg} made");

        public void LegEnded(string leg) => Add($"{leg} ended");

        public void Used(FlowTurnSpend turn) => Add("used");

        public void Held(IReadOnlyList<string> on, IReadOnlyList<QueueAhead> ahead)
            => Add(on.Count == 0 ? "a leg runs" : $"held on {string.Join(", ", on)}{(ahead.Count == 0 ? string.Empty : $", behind {string.Join(", ", ahead.Select(standing => $"{QueueReports.NameOf(standing.Class)}: {string.Join(", ", standing.Tenants)}"))}")}");

        public void HandoffTaken() => Add("handoff taken");

        private void Add(string said)
        {
            lock (_said)
            {
                _said.Add(said);
            }
        }
    }

    private static async Task<OrchestratorLayout> LayoutAsync(HarnessFactory harness, TempDirectory repository)
        => OrchestratorLayout.Of((await harness.ContextLoader.LoadAsync(repository.Path, Token)).Layout, "r1");

    /// <summary>
    /// A leg step tells the engine where each leg works as its use case's leg service says it - each leg placed, in a copy
    /// or in the tree, then that every leg is placed; each copy made; each leg ended - in that order, and answers each
    /// leg's verdict, no red leg and where the run's records are. The ledger, as --json gives it, is kept in the step's
    /// directory; a step on the main tree keeps no log.
    /// </summary>
    [Fact]
    public async Task ALegStep_TellsTheEngineWhereEachLegWorks_AndKeepsTheLedger()
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: false));
        var legs = new FakeLegs
        {
            Answer = Result(
                [
                    new LegEntry
                    {
                        Leg = "native",
                        Verdict = LegVerdict.Passed,
                        Duration = TimeSpan.FromSeconds(90),
                        Phases = [new PhaseRecord("configure", TimeSpan.FromSeconds(20), false), new PhaseRecord("build", TimeSpan.FromSeconds(65), false)],
                        Admission = new AdmissionFact(true, 42.5),
                        Digest = ResultDigest.Of(
                            [
                                new Diagnostic("msbuild", "warning", "CS0168", "unused", "src/a.cs", 1),
                                new Diagnostic("msbuild", "warning", "CS0168", "unused", "src/b.cs", 2),
                                new Diagnostic("msbuild", "warning", "CS0219", "assigned", "src/c.cs", 3),
                            ],
                            [],
                            ["a line no parser read"]),
                    },
                    new LegEntry { Leg = "here", Verdict = LegVerdict.Passed },
                ],
                HarnessExit.Success,
                "passed"),
            WhileItRuns = watch =>
            {
                watch.Placed([new LegPlace("native", InACopy: true), new LegPlace("here", InACopy: false)]);
                watch.CopyMade("native");
                watch.Ended("here");
                watch.Admitting("native", "native", new AdmissionTold(AdmissionStage.Queue, new QueueAhead(DispatchClassName.Heavy, ["local/src/other"])));
                watch.Admitting("native", "native", new AdmissionTold(AdmissionStage.Memory));
                watch.Admitting("native", "native", new AdmissionTold(AdmissionStage.Taken));
                watch.Admitting("native", "native", new AdmissionTold(AdmissionStage.Ended));
                watch.Ended("native");
            },
        };
        var said = new Said();
        var runner = new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.GraphSyncs);

        var result = await runner.RunAsync(Run(new WorkflowStep { Name = "checks", Type = "build" }, repository.Path, said), Token);

        Assert.True(result.Passed, result.Error);
        Assert.Equal(HarnessExit.Success, result.Exit);
        Assert.Equal([new FlowLeg("native", LegVerdict.Passed), new FlowLeg("here", LegVerdict.Passed)], result.Legs);

        // The machine held the step once every leg still to run waited at it - each cause as it began, the queue's with
        // who stands ahead - and let it go on as its leg started.
        Assert.Equal(
            [
                "placed native in a copy", "placed here in the tree", "every leg placed", "copy of native made", "here ended",
                $"held on {FlowWaits.MachineQueue}, behind heavy: local/src/other", $"held on {FlowWaits.MachineMemory}", "a leg runs", "native ended",
            ],
            said.Events);

        // What each leg did is the attempt's: its time, how long its machine held it, each phase and its digest's counts.
        Assert.Equal(
            JsonSerializer.Serialize<IReadOnlyList<FlowStepLeg>>(
            [
                new FlowStepLeg("native", "passed", 90, 42.5, [new FlowStepPhase("configure", 20), new FlowStepPhase("build", 65)], 0, 3, 0, 1),
                new FlowStepLeg("here", "passed"),
            ]),
            JsonSerializer.Serialize(result.Attempt!.Legs));
        Assert.Equal(
            """{"passed":true,"exitCode":0,"verdicts":[{"leg":"native","verdict":"passed"},{"leg":"here","verdict":"passed"}],"red":[],"runDirectory":"/runs/r","isolation":{"mode":"required","none":"the test asked none"}}""",
            result.Output!.ToJsonString());

        var step = (await LayoutAsync(harness, repository)).InstanceDirectory("checks", agent: null);
        var ledger = JsonNode.Parse(File.ReadAllText(Path.Combine(step, OrchestratorLayout.StepLedgerFileName)))!;
        Assert.Equal(["native", "here"], ledger["legs"]!.AsArray().Select(leg => leg!["leg"]!.GetValue<string>()));
        Assert.False(Directory.Exists(Path.Combine(step, OrchestratorLayout.StepLogsDirectoryName)));
    }

    /// <summary>
    /// A leg step runs its legs under a process scope of its own - what they start bound is apart from the engine's
    /// terminal, and recorded while it runs - whose records are kept in the step's own directory, a run's step's and an
    /// agent's alike, so the next command over the run ends what a killed engine left.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALegStep_RunsItsLegsUnderAProcessScope_RecordingInItsOwnDirectory(bool inAnAgent)
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: false));
        var layout = inAnAgent ? (await AgentAsync(harness, repository)).Layout : await LayoutAsync(harness, repository);
        var legs = new FakeLegs();
        var runner = new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.GraphSyncs);
        var step = new WorkflowStep { Name = "checks", Type = "build", In = inAnAgent ? "make" : null };

        var result = await runner.RunAsync(Run(step, repository.Path, worksIn: inAnAgent ? "a1" : null), Token);

        Assert.True(result.Passed, result.Error);

        var record = Assert.IsType<ProcessScope>(legs.Scope).Record();
        var file = OrchestratorLayout.ChildFile(layout.InstanceDirectory("checks", inAnAgent ? "a1" : null), Environment.ProcessId);

        record.Placed(Environment.ProcessId, started: null, TestHost.AssemblyProgram, group: true);

        Assert.True(File.Exists(file), $"the scope's record is not at '{file}'");
        Assert.True(JsonNode.DeepEquals(JsonValue.Create(true), JsonNode.Parse(File.ReadAllText(file))!["group"]), File.ReadAllText(file));

        record.Ended();

        Assert.False(File.Exists(file));
    }

    /// <summary>
    /// A step in an agent's worktree runs its legs there and keeps, whole, the log of each red leg's phase that did not
    /// pass where this machine ran the leg - not a green leg's, not a phase that passed, not a leg a host ran, whose log
    /// stays on that host - inside the agent's directory; on the main tree the same legs leave the ledger alone.
    /// </summary>
    [Fact]
    public async Task InAnAgentsWorktree_EachRedLegsFailingLog_IsKept()
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: false));
        var (layout, worktree) = await AgentAsync(harness, repository);

        var logs = repository.Combine("logs");
        Directory.CreateDirectory(logs);
        string Log(string name, string text)
        {
            var file = Path.Combine(logs, name);
            File.WriteAllText(file, text);
            return file;
        }

        var legs = new FakeLegs
        {
            Answer = Result(
                [
                    new LegEntry
                    {
                        Leg = "native",
                        Verdict = LegVerdict.Failed,
                        TailPhase = "test",
                        LogTail = ["boom"],
                        Logs = [new PhaseLogFile("build", Log("native.build", "built\n")), new PhaseLogFile("test", Log("native.test", "a test failed\nboom\n"))],
                    },
                    new LegEntry { Leg = "green", Verdict = LegVerdict.Passed, Logs = [new PhaseLogFile("test", Log("green.test", "ok\n"))] },
                    new LegEntry { Leg = "far", Verdict = LegVerdict.Failed, TailPhase = "test", HostRunId = RunId.New(), Logs = [new PhaseLogFile("test", Log("far.test", "on the host\n"))] },
                ],
                HarnessExit.CommandFailed,
                "a leg failed"),
        };
        var runner = new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.GraphSyncs);
        var step = new WorkflowStep { Name = "checks", Type = "build", In = "make" };

        var result = await runner.RunAsync(Run(step, repository.Path, worksIn: "a1"), Token);

        Assert.Equal(worktree, legs.Asked.Single().Directory);
        Assert.Equal(["native", "far"], result.Output!["red"]!.AsArray().Select(red => red!["leg"]!.GetValue<string>()));
        var kept = Path.Combine(layout.InstanceDirectory("checks", "a1"), OrchestratorLayout.StepLogsDirectoryName);
        Assert.Equal(["native.test.log"], Directory.EnumerateFiles(kept).Select(Path.GetFileName));
        Assert.Equal("a test failed\nboom\n", File.ReadAllText(Path.Combine(kept, "native.test.log")));
        Assert.True(File.Exists(Path.Combine(layout.InstanceDirectory("checks", "a1"), OrchestratorLayout.StepLedgerFileName)));

        // Each red entry names the log its digest's ranges number, one read away: the copy the step kept, and none for a
        // leg a host ran, whose log stays there.
        Assert.Equal(
            [Path.Combine(kept, "native.test.log"), null],
            result.Output!["red"]!.AsArray().Select(red => red!["log"]?.GetValue<string>()));

        var onMain = await runner.RunAsync(Run(step with { Name = "main-checks", In = null }, repository.Path), Token);

        Assert.Equal(repository.Path, legs.Asked[^1].Directory);
        Assert.Equal(HarnessExit.CommandFailed, onMain.Exit);
        Assert.True(File.Exists(Path.Combine(layout.InstanceDirectory("main-checks", null), OrchestratorLayout.StepLedgerFileName)));
        Assert.False(Directory.Exists(Path.Combine(layout.InstanceDirectory("main-checks", null), OrchestratorLayout.StepLogsDirectoryName)));

        // On the main tree, where no copy is kept, the log its run's records hold.
        Assert.Equal(Path.Combine(logs, "native.test"), onMain.Output!["red"]![0]!["log"]!.GetValue<string>());
    }

    /// <summary>
    /// Where the run keeps a graph of the tree a leg step's legs ran in, each red leg's digest is joined to the declarations
    /// the graph holds at its places in the tree, checked first by the files they name: the first error's symbol is the
    /// member enclosing its line - its kind, its qualified name and its handle in the view - a frame of a failure's stack in
    /// the tree is rendered with it, a frame outside the tree as printed, and the ledger the step keeps holds the same. A
    /// run that keeps no graph says none, and the step makes none for it.
    /// </summary>
    [Fact]
    public async Task ARedLegsDigest_IsJoinedToTheDeclarationsOfTheRunsGraph_WhereTheRunKeepsOne()
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: false));
        repository.WriteFile(
            "src/Store.cs",
            "namespace App;\n\npublic sealed class Store\n{\n    public int Load(string name)\n    {\n        var found = 0;\n        return found + x;\n    }\n}\n");
        await harness.CommitAllAsync(repository.Path, "the store", Token);

        var digest = ResultDigest.Of(
            [new Diagnostic("csc", "error", "CS0103", "The name 'x' does not exist in the current context", "src/Store.cs", 8, 24)],
            [
                new TestFailure(
                    "xunit",
                    "App.Tests.StoreTests.Loads",
                    Exception: new ExceptionRecord(
                        "System.InvalidOperationException",
                        "no record",
                        [
                            new StackFrame("at App.Store.Load(String name) in src/Store.cs:line 7", "src/Store.cs", 7, Symbol: "App.Store.Load", InTree: true),
                            new StackFrame("at System.Linq.Enumerable.First()", Symbol: "System.Linq.Enumerable.First"),
                        ],
                        [])),
            ],
            []);
        var legs = new FakeLegs
        {
            Answer = Result([new LegEntry { Leg = "native", Verdict = LegVerdict.Failed, TailPhase = "build", Digest = digest }], HarnessExit.CommandFailed, "a leg failed"),
        };
        var runner = new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.GraphSyncs);
        var layout = await LayoutAsync(harness, repository);

        var ungraphed = await runner.RunAsync(Run(new WorkflowStep { Name = "plain", Type = "build" }, repository.Path), Token);

        Assert.Null(ungraphed.Output!["red"]![0]!["digest"]!["first"]!["symbol"]);
        Assert.False(File.Exists(layout.GraphManifestFile));

        var context = await harness.ContextLoader.LoadAsync(repository.Path, Token);
        await harness.CodebaseGraph.SyncAsync(FlowGraphSyncs.RequestOf(context, new FlowRunPlace(repository.Path, "r1", null, Flow), "test"), Token);
        var view = new DssHarness.Core.Codebase.CodebaseGraphQueries((await harness.CodebaseGraph.ReadOnlyAsync(layout, null, Token))!);
        var load = view.AtLine("src/Store.cs", 8)!;
        var said = $"member App.Store.Load {load.Handle}";

        var graphed = await runner.RunAsync(Run(new WorkflowStep { Name = "graphed", Type = "build" }, repository.Path), Token);

        // The graph's own answer at the place, named as the join names it: a member, its qualified name, its handle.
        Assert.Equal(("member", "App.Store.Load"), (load.Kind, load.Qualified));
        var entry = graphed.Output!["red"]![0]!;
        Assert.Equal(said, entry["digest"]!["first"]!["symbol"]!.GetValue<string>());
        Assert.Equal(
            [$"at App.Store.Load(String name) in src/Store.cs:line 7 [{said}]", "at System.Linq.Enumerable.First()"],
            entry["digest"]!["failures"]![0]!["stack"]!.AsArray().Select(line => line!.GetValue<string>()));

        var ledger = JsonNode.Parse(File.ReadAllText(Path.Combine(layout.InstanceDirectory("graphed", null), OrchestratorLayout.StepLedgerFileName)))!;
        var kept = ledger["legs"]![0]!["digest"]!;
        Assert.Equal(said, kept["diagnostics"]![0]!["symbol"]!.GetValue<string>());
        Assert.Equal($"App.Store.Load [{said}]", kept["failures"]![0]!["exception"]!["stack"]![0]!["symbol"]!.GetValue<string>());
    }

    /// <summary>
    /// The logs a step keeps are its last attempt's alone, each found and kept as its phase's log is named - a runner's
    /// step whose name holds a character a path cannot carry among them - and an attempt whose legs all passed keeps none,
    /// whatever the attempt before it kept.
    /// </summary>
    [Fact]
    public async Task TheKeptLogs_AreTheLastAttemptsAlone_NamedAsTheirPhasesLogsAre()
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: false));
        var (layout, _) = await AgentAsync(harness, repository);
        var file = repository.Combine("native.step (2-2).log");
        File.WriteAllText(file, "the second line failed\n");
        var red = new FakeLegs
        {
            Answer = Result(
                [new LegEntry { Leg = "native", Verdict = LegVerdict.Failed, TailPhase = "step (2/2)", LogTail = ["the second line failed"], Logs = [new PhaseLogFile("step (2-2)", file)] }],
                HarnessExit.CommandFailed,
                "a leg failed"),
        };
        var step = new WorkflowStep { Name = "shape", Type = "build", In = "make" };
        var kept = Path.Combine(layout.InstanceDirectory("shape", "a1"), OrchestratorLayout.StepLogsDirectoryName);

        var failed = await new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, red, new TestIsolation(), harness.GraphSyncs)
            .RunAsync(Run(step, repository.Path, worksIn: "a1"), Token);

        Assert.Equal("step (2/2)", failed.Output!["red"]![0]!["phase"]!.GetValue<string>());
        Assert.Equal(["native.step (2-2).log"], Directory.EnumerateFiles(kept).Select(Path.GetFileName));
        Assert.Equal("the second line failed\n", File.ReadAllText(Path.Combine(kept, "native.step (2-2).log")));

        var passed = await new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, new FakeLegs(), new TestIsolation(), harness.GraphSyncs)
            .RunAsync(Run(step, repository.Path, worksIn: "a1"), Token);

        Assert.True(passed.Passed, passed.Error);
        Assert.False(Directory.Exists(kept));
    }

    /// <summary>
    /// A workflow's run step, run by the tool itself: its leg works in a copy of its own and the step's record says the
    /// tree was let go once the copy was made; its output answers the leg's verdict and the isolation its leg's work was
    /// held to - this machine's, or none where it has none and the configuration prefers it - and its ledger is kept
    /// beside it.
    /// </summary>
    [Fact]
    public async Task ARunOfAWorkflow_RunsItsLegStep_AndRecordsTheTreeLetGo()
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: true, workflow: "flow", isolation: "preferred"));
        repository.WriteFile(".harness-config/workflows/flow/flow.yml", """
            name: flow
            steps:
              - name: probe
                type: run
                runner: probe
            """);

        var run = await CliRunner.RunAsync(["run-orchestrator", "flow", "--run", "r1", "-C", repository.Path], Token);

        Assert.True(run.ExitCode == HarnessExit.Success, $"{run.ExitCode}: {run.StandardOutput}{run.StandardError}");

        var step = (await LayoutAsync(harness, repository)).InstanceDirectory("probe", agent: null);
        var record = JsonNode.Parse(File.ReadAllText(Path.Combine(step, OrchestratorLayout.StepRecordFileName)))!;
        var output = JsonNode.Parse(File.ReadAllText(Path.Combine(step, OrchestratorLayout.StepOutputFileName)))!;
        var ledger = JsonNode.Parse(File.ReadAllText(Path.Combine(step, OrchestratorLayout.StepLedgerFileName)))!;

        Assert.Equal("Done", record["state"]!.GetValue<string>());
        Assert.True(record["copied"]!.GetValue<bool>());
        Assert.Equal("""[{"leg":"native","verdict":"passed"}]""", output["verdicts"]!.ToJsonString());
        Assert.Equal("preferred", output["isolation"]!["mode"]!.GetValue<string>());
        Assert.Equal("passed", ledger["legs"]![0]!["verdict"]!.GetValue<string>());
    }

    /// <summary>
    /// Where this machine has no isolation, a leg step under required runs no leg - refused 14, why, then the remedy - and
    /// under preferred runs them, answering the isolation it ran under. Each leg's own is asked of the tree it works in, its
    /// records written, and a sweep's of the workers it makes beside them.
    /// </summary>
    [Fact]
    public async Task WithNoIsolation_ALegStepIsRefusedUnderRequired_AndRunsSayingItUnderPreferred()
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: false));
        var legs = new FakeLegs();
        var none = TestIsolation.UnavailableToExecutions("bubblewrap is not here", "install it");
        var step = new WorkflowStep { Name = "checks", Type = "build" };

        var refused = await new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, none, harness.GraphSyncs)
            .RunAsync(Run(step, repository.Path), Token);

        Assert.Equal(
            (false, HarnessExit.ToolMissing, "'checks' is refused: agentPolicy.isolation is \"required\" and bubblewrap is not here, so nothing the agents wrote is built, tested or run here; install it. Nothing was run."),
            (refused.Passed, refused.Exit, refused.Error));
        Assert.Empty(legs.Asked);
        Assert.Equal(IsolatedExecution.Legs, Assert.Single(none.Executions).Kind);
        Assert.Equal([repository.Path], none.Executions[0].WritableRoots);

        var (preferring, preferred) = await RepositoryAsync(Config(localCopy: false, isolation: "preferred"));
        var ran = await new BuildStepRunner(preferring.ContextLoader, preferring.Platform, preferring.OrchestrationStore, preferring.FileSystem, preferring.ChildSessions, legs, none, preferring.GraphSyncs)
            .RunAsync(Run(step, preferred.Path), Token);

        Assert.True(ran.Passed, ran.Error);
        Assert.Equal("""{"mode":"preferred","none":"bubblewrap is not here"}""", ran.Output!["isolation"]!.ToJsonString());

        var sweeping = new TestIsolation();
        _ = await new SweepStepRunner(preferring.ContextLoader, preferring.Platform, preferring.OrchestrationStore, preferring.FileSystem, preferring.ChildSessions, legs, sweeping, preferring.WorktreeService, preferring.GraphSyncs)
            .RunAsync(Run(new WorkflowStep { Name = "arms", Type = "check-mutations" }, preferred.Path), Token);
        var copy = Path.Combine(preferred.Path, HarnessLayout.LegsDirectoryName, "a-copy");
        var actionBuild = Path.Combine(copy, HarnessLayout.ActionBuildRelative("probe", "r9", "native"));
        _ = await legs.Asked[^1].Isolation!.ForLegAsync(copy, [], Token);
        _ = await legs.Asked[^2].Isolation!.ForLegAsync(copy, [], Token);

        Assert.Equal([copy, Path.Combine(copy, HarnessLayout.MutationsDirectoryName)], sweeping.Executions[^1].WritableRoots);
        Assert.Equal([preferred.Path, copy], sweeping.Executions[^1].Trees.Skip(sweeping.Executions[^1].Trees.Count - 2));
        Assert.Equal([copy], none.Executions[^1].WritableRoots);

        // What the leg's work says it writes in the tree's configuration directory - an action's build directory for the
        // run - is written beside the tree, and beside a sweep's workers.
        _ = await legs.Asked[^1].Isolation!.ForLegAsync(copy, [actionBuild], Token);
        _ = await legs.Asked[^2].Isolation!.ForLegAsync(copy, [actionBuild], Token);

        Assert.Equal([copy, Path.Combine(copy, HarnessLayout.MutationsDirectoryName), actionBuild], sweeping.Executions[^1].WritableRoots);
        Assert.Equal([copy, actionBuild], none.Executions[^1].WritableRoots);
    }

    /// <summary>
    /// A step whose in names an agent the run made none for is the engine's fault (70), and one whose agent has no
    /// worktree - read-only, or deleted - is refused (13): neither runs a leg.
    /// </summary>
    [Fact]
    public async Task AStepWorkingInNoWorktree_RunsNothing()
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: false));
        var legs = new FakeLegs();
        var runner = new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.GraphSyncs);
        var step = new WorkflowStep { Name = "checks", Type = "build", In = "make" };

        var unmade = await runner.RunAsync(Run(step, repository.Path), Token);

        Assert.Equal(HarnessExit.InternalError, unmade.Exit);
        Assert.Equal("'checks' works in the agent 'make' makes, and the run has made none for it", unmade.Error);

        var none = await runner.RunAsync(Run(step, repository.Path, worksIn: "a7"), Token);

        Assert.Equal(HarnessExit.Refused, none.Exit);
        Assert.Equal("'checks' works in agent 'a7' of 'r1', which has no worktree for its legs to run in. Nothing was run.", none.Error);
        Assert.Empty(legs.Asked);
    }

    /// <summary>
    /// Each leg step asks its command's use case what its keys say, in the tree it works in: the legs it names, or none
    /// for every leg; whether a build, a test or a run that builds keeps going past the units its builds fail; a test's
    /// filter, exclusions, labels and skipBuild; a run's runner, inputs and - for a run that writes the tree, by its runner
    /// or a run check's - in place; a sweep's arms, or none for every arm, and pending.
    /// </summary>
    [Fact]
    public async Task EachLegStep_AsksItsUseCase_WhatItsKeysSay()
    {
        var config = Config(localCopy: false);
        config.PredefinedRunners["tidy"] = new RunnerConfig { WritesTree = true, Phases = [new RunnerPhase { Name = "fix", Command = ["dotnet", "--version"] }] };
        var (harness, repository) = await RepositoryAsync(config);
        var legs = new FakeLegs();
        IFlowStepTypeRunner[] runners =
        [
            new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.GraphSyncs),
            new TestStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.GraphSyncs),
            new RunStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.GraphSyncs),
            new SweepStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.WorktreeService, harness.GraphSyncs),
        ];
        WorkflowStep[] steps =
        [
            new() { Name = "build", Type = "build" },
            new() { Name = "test", Type = "test", Legs = ["native"], Filter = new FlowList(["fast", "smoke"], Expression: null), Exclude = ["slow"], Labels = ["unit"], SkipBuild = true },
            new() { Name = "run", Type = "run", Runner = "probe", Inputs = new Dictionary<string, string> { ["target"] = "all" }, KeepGoing = true },
            new() { Name = "tidy", Type = "run", Runner = "TIDY" },
            new() { Name = "build past", Type = "build", KeepGoing = true },
            new() { Name = "test past", Type = "test", KeepGoing = true },
            new() { Name = "sweep", Type = "check-mutations" },
            new() { Name = "pending", Type = "check-mutations", Arms = new FlowList(["A-ONE"], Expression: null), Pending = true },
        ];

        foreach (var step in steps)
        {
            var result = await runners.Single(runner => runner.Type == step.Type).RunAsync(Run(step, repository.Path), Token);
            Assert.True(result.Passed, result.Error);
        }

        Assert.Collection(
            legs.Asked,
            asked => Assert.Equal(new BuildLegsRequest { Directory = repository.Path, Watch = asked.Watch, Isolation = asked.Isolation }, asked),
            asked => Assert.Equivalent(
                new TestLegsRequest { Directory = repository.Path, LegNames = ["native"], Filters = ["fast", "smoke"], Excludes = ["slow"], Labels = ["unit"], SkipBuild = true, Watch = asked.Watch, Isolation = asked.Isolation },
                asked,
                strict: true),
            asked => Assert.Equivalent(
                new RunnerLegsRequest { Directory = repository.Path, Runner = "probe", Inputs = new Dictionary<string, string> { ["target"] = "all" }, KeepGoing = true, Watch = asked.Watch, Isolation = asked.Isolation },
                asked,
                strict: true),
            asked => Assert.Equivalent(new RunnerLegsRequest { Directory = repository.Path, Runner = "TIDY", InPlace = true, Watch = asked.Watch, Isolation = asked.Isolation }, asked, strict: true),
            asked => Assert.Equal(new BuildLegsRequest { Directory = repository.Path, KeepGoing = true, Watch = asked.Watch, Isolation = asked.Isolation }, asked),
            asked => Assert.Equivalent(new TestLegsRequest { Directory = repository.Path, KeepGoing = true, Watch = asked.Watch, Isolation = asked.Isolation }, asked, strict: true),
            asked => Assert.Equal(new MutationLegsRequest { Directory = repository.Path, Watch = asked.Watch, Isolation = asked.Isolation }, asked),
            asked => Assert.Equivalent(new MutationLegsRequest { Directory = repository.Path, ArmNames = ["A-ONE"], Pending = true, Watch = asked.Watch, Isolation = asked.Isolation }, asked, strict: true));
        Assert.All(legs.Asked, asked => Assert.NotNull(asked.Watch));
        Assert.All(legs.Asked, asked => Assert.NotNull(asked.Isolation));
    }

    /// <summary>
    /// Every leg step asks its legs at its run's priority, as the run's workflow declares it - a build's, a test's, a
    /// run's and a sweep's alike - so every unit of the run is charged against its tenant's share by the one priority;
    /// a workflow that declares none asks at 1, and a step inside a use that governs itself asks at the priority the
    /// used workflow declares.
    /// </summary>
    [Fact]
    public async Task EachLegStep_AsksItsLegsAtItsRunsPriority()
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: false));
        var legs = new FakeLegs();
        IFlowStepTypeRunner[] runners =
        [
            new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.GraphSyncs),
            new TestStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.GraphSyncs),
            new RunStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.GraphSyncs),
            new SweepStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.WorktreeService, harness.GraphSyncs),
        ];
        var urgent = new FlowRunPlace(repository.Path, "r1", null, Flow with { Priority = 3 });
        var governing = new WorkflowStep
        {
            Name = "items",
            Use = new FlowUse("./pipeline/pipeline.yml", "flow/pipeline", "fingerprint", [], [], [], null, [], new FlowUseOwn(Priority: 7)),
        };
        var governed = new FlowRunPlace(repository.Path, "r1", null, Flow with { Priority = 3, Steps = [governing] });

        foreach (var runner in runners)
        {
            var step = new WorkflowStep { Name = runner.Type, Type = runner.Type, Runner = runner.Type == "run" ? "probe" : null };
            var declared = await runner.RunAsync(Run(step, repository.Path, instance: $"{runner.Type}-urgent") with { Place = urgent }, Token);
            var undeclared = await runner.RunAsync(Run(step, repository.Path, instance: $"{runner.Type}-plain"), Token);
            var used = await runner.RunAsync(Run(step, repository.Path, instance: $"{runner.Type}-used") with { Place = governed, Path = $"items.{runner.Type}" }, Token);

            Assert.True(declared.Passed, declared.Error);
            Assert.True(undeclared.Passed, undeclared.Error);
            Assert.True(used.Passed, used.Error);
        }

        Assert.Equal(
            [("build", 3), ("build", 1), ("build", 7), ("test", 3), ("test", 1), ("test", 7), ("run", 3), ("run", 1), ("run", 7), ("check-mutations", 3), ("check-mutations", 1), ("check-mutations", 7)],
            legs.Asked.Select(asked => (asked switch { BuildLegsRequest => "build", TestLegsRequest => "test", RunnerLegsRequest => "run", _ => "check-mutations" }, asked.Priority)));
    }

    /// <summary>
    /// A sweep that names a commit in <c>base</c> runs on a copy of that commit - made and taken away under the main
    /// tree's lock, so other items' folds never move the bytes under it - and <c>item</c> is the newest commit the
    /// run's ledger attributes to the item the step is one of. Its arms it takes from what the engine read where it
    /// started, pending as it says, and an item whose ledger holds no commit is refused, nothing swept.
    /// </summary>
    [Fact]
    public async Task ASweepThatNamesACommit_RunsItsArmsPendingOnACopyOfIt_TakenAwayAfter()
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: false));
        OrchestrationKit.Write(repository.Path, "src/rate.cs", "the rate\n");
        await harness.CommitAllAsync(repository.Path, "the item's fold", Token);
        var commit = (await harness.GitClient.ResolveCommitAsync(repository.Path, "HEAD", Token))!;
        var layout = OrchestratorLayout.Of(new HarnessLayout(repository.Path, repository.Path), "r1");

        harness.OrchestrationStore.UpdateFolds(
            layout,
            ledger => ledger with
            {
                Folds = [new FlowFoldEntry("f1", "rounds#1.items[0].fold", 1, "fold", "a1", FlowFoldEntry.Intended, DateTimeOffset.UtcNow, [new FlowFoldPath("src/rate.cs", "written", null)], [])],
                Commits = [new FlowCommitEntry("c1", "rounds#1.items[0].checkpoint", 1, FlowCommitEntry.Committed, null, [new FlowCommitPath("src/rate.cs", null)], ["f1"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Sha = commit }],
            });

        string? copy = null;
        string? swept = null;
        FakeLegs? made = null;
        FakeLegs legs = new()
        {
            WhileItRuns = _ =>
            {
                copy = made!.Asked[^1].Directory;
                swept = File.ReadAllText(Path.Combine(copy!, "src", "rate.cs"));
            },
        };
        made = legs;
        var runner = new SweepStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.WorktreeService, harness.GraphSyncs);

        var byItem = await runner.RunAsync(
            Run(
                new WorkflowStep { Name = "item-arms", Type = "check-mutations", Base = SweepStepRunner.ItemBase, Pending = true, Arms = new FlowList([], Expression: "implement.latest.arms") },
                repository.Path,
                read: new Dictionary<string, FlowValue> { ["arms"] = FlowValue.Of(new JsonArray("A-ONE")) },
                instance: "rounds#1.items[0].item-arms"),
            Token);

        Assert.True(byItem.Passed, byItem.Error);
        PathAssert.Same(Path.Combine(layout.TempDirectory, "sweep-rounds#1.items@0.item-arms"), copy!);
        Assert.Equal("the rate\n", swept);
        Assert.False(Directory.Exists(copy), $"{copy} was left behind");
        Assert.Empty(await CopiesOfAsync(harness, repository.Path));
        Assert.Equal(new JsonObject { ["path"] = copy, ["left"] = null }.ToJsonString(), byItem.Output!["copy"]!.ToJsonString());
        Assert.Equivalent(
            new MutationLegsRequest { Directory = copy!, ArmNames = ["A-ONE"], Pending = true, Watch = legs.Asked[0].Watch, Isolation = legs.Asked[0].Isolation },
            legs.Asked[0],
            strict: true);

        // A base naming a commit outright sweeps a copy of that commit.
        legs.Asked.Clear();
        _ = await runner.RunAsync(
            Run(new WorkflowStep { Name = "sweep", Type = "check-mutations", Base = commit }, repository.Path, instance: "rounds#1.items[0].item-arms"),
            Token);

        Assert.NotEqual(repository.Path, legs.Asked[0].Directory);

        // An item whose ledger holds no commit is refused, nothing swept.
        var none = await runner.RunAsync(
            Run(new WorkflowStep { Name = "sweep", Type = "check-mutations", Base = SweepStepRunner.ItemBase }, repository.Path, instance: "rounds#2.items[0].item-arms"),
            Token);

        Assert.False(none.Passed);
        Assert.Equal(HarnessExit.Refused, none.Exit);
        Assert.Equal("'rounds#2.items[0].item-arms' sweeps the arms of the item's own newest commit, and the run's ledger holds no commit of item 'rounds#2.items[0]' that was made. Nothing was swept.", none.Error);
        Assert.Single(legs.Asked);

        // A base that names nothing as the step starts - a template whose value was blank - is refused, never read as no
        // base and swept on the main tree.
        var blank = await runner.RunAsync(
            Run(new WorkflowStep { Name = "sweep", Type = "check-mutations", Base = " " }, repository.Path, instance: "rounds#1.items[0].item-arms"),
            Token);

        Assert.False(blank.Passed);
        Assert.Equal(HarnessExit.Refused, blank.Exit);
        Assert.Equal("'rounds#1.items[0].item-arms' sweeps a copy of the commit its base names, and its base named none as it started: it was blank. Nothing was swept.", blank.Error);
        Assert.Single(legs.Asked);
    }

    /// <summary>
    /// The item's own newest commit is the newest the ledger holds as made: of two commits of the item the later one -
    /// a rearm's refold, never the first checkpoint - whatever another item committed after it, and never a set still
    /// staged or one git refused, which names no commit.
    /// </summary>
    [Fact]
    public async Task ASweepOfTheItemsOwnCommit_TakesItsNewestMadeCommit_NoOtherItemsAndNoneUnmade()
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: false));
        var layout = OrchestratorLayout.Of(new HarnessLayout(repository.Path, repository.Path), "r1");
        var shas = new List<string>();

        foreach (var text in new[] { "first\n", "refold\n", "another item\n" })
        {
            OrchestrationKit.Write(repository.Path, "src/rate.cs", text);
            await harness.CommitAllAsync(repository.Path, text.Trim(), Token);
            shas.Add((await harness.GitClient.ResolveCommitAsync(repository.Path, "HEAD", Token))!);
        }

        var at = DateTimeOffset.UtcNow;
        harness.OrchestrationStore.UpdateFolds(
            layout,
            ledger => ledger with
            {
                Folds =
                [
                    new FlowFoldEntry("f1", "rounds#1.items[0].fold", 1, "fold", "a1", FlowFoldEntry.Intended, at, [new FlowFoldPath("src/rate.cs", "written", null)], []),
                    new FlowFoldEntry("f2", "rounds#1.items[0].rearm#1.rearm-fold", 1, "fold", "a1", FlowFoldEntry.Intended, at, [new FlowFoldPath("src/rate.cs", "written", null)], []),
                    new FlowFoldEntry("f3", "rounds#1.items[1].fold", 1, "fold", "a2", FlowFoldEntry.Intended, at, [new FlowFoldPath("src/rate.cs", "written", null)], []),
                    new FlowFoldEntry("f4", "rounds#1.items[0].fix#1.revise.refold", 1, "fold", "a1", FlowFoldEntry.Intended, at, [new FlowFoldPath("src/rate.cs", "written", null)], []),
                    new FlowFoldEntry("f5", "rounds#1.items[0].fix#2.revise.refold", 1, "fold", "a1", FlowFoldEntry.Intended, at, [new FlowFoldPath("src/rate.cs", "written", null)], []),
                ],
                Commits =
                [
                    new FlowCommitEntry("c1", "rounds#1.items[0].checkpoint", 1, FlowCommitEntry.Committed, null, [new FlowCommitPath("src/rate.cs", null)], ["f1"], at, at) { Sha = shas[0] },
                    new FlowCommitEntry("c2", "rounds#1.items[0].rearm#1.rearm-checkpoint", 1, FlowCommitEntry.Committed, null, [new FlowCommitPath("src/rate.cs", null)], ["f2"], at, at) { Sha = shas[1] },
                    new FlowCommitEntry("c3", "rounds#1.items[1].checkpoint", 1, FlowCommitEntry.Committed, null, [new FlowCommitPath("src/rate.cs", null)], ["f3"], at, at) { Sha = shas[2] },
                    new FlowCommitEntry("c4", "rounds#1.items[0].fix#1.revise.recheckpoint", 1, FlowCommitEntry.Unstaged, null, [new FlowCommitPath("src/rate.cs", null)], ["f4"], at, at),
                    new FlowCommitEntry("c5", "rounds#1.items[0].fix#2.revise.recheckpoint", 1, FlowCommitEntry.Staged, null, [new FlowCommitPath("src/rate.cs", null)], ["f5"], at),
                ],
            });

        string? swept = null;
        FakeLegs? made = null;
        var legs = new FakeLegs { WhileItRuns = _ => swept = File.ReadAllText(Path.Combine(made!.Asked[^1].Directory, "src", "rate.cs")) };
        made = legs;

        var result = await new SweepStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.WorktreeService, harness.GraphSyncs)
            .RunAsync(Run(new WorkflowStep { Name = "item-arms", Type = "check-mutations", Base = SweepStepRunner.ItemBase }, repository.Path, instance: "rounds#1.items[0].rearm#1.item-arms"), Token);

        Assert.True(result.Passed, result.Error);
        Assert.Equal("refold\n", swept);
    }

    /// <summary>
    /// A copy an earlier attempt of the same sweep left at its path - its directory with git's record of it, a stop, a
    /// pause or a crash before it was taken away - is taken away before the copy is made again, and the sweep runs; and
    /// one a stop of the step cut off is taken away all the same, the stop's cancellation never reaching the removal:
    /// nothing is left, of the directory or of git's record.
    /// </summary>
    [Fact]
    public async Task ASweepsCopy_IsTakenAwayAfterAStop_AndOneAnEarlierAttemptLeft_IsTakenAwayFirst()
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: false));
        var commit = (await harness.GitClient.ResolveCommitAsync(repository.Path, "HEAD", Token))!;
        var layout = OrchestratorLayout.Of(new HarnessLayout(repository.Path, repository.Path), "r1");
        var path = Path.Combine(layout.TempDirectory, SweepStepRunner.CopyNameOf("rounds#1.items[0].item-arms"));
        var step = new WorkflowStep { Name = "item-arms", Type = "check-mutations", Base = commit };

        // A copy an earlier attempt made and never took away, with what its sweep wrote in it.
        Assert.True((await harness.WorktreeService.CreateCopyAsync(repository.Path, path, commit, Token)).Succeeded);
        File.WriteAllText(Path.Combine(path, "left.txt"), "written by the attempt a crash ended\n");

        var legs = new FakeLegs();
        var runner = new SweepStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.WorktreeService, harness.GraphSyncs);
        var again = await runner.RunAsync(Run(step, repository.Path, instance: "rounds#1.items[0].item-arms"), Token);

        Assert.True(again.Passed, again.Error);
        PathAssert.Same(path, legs.Asked.Single().Directory);
        Assert.False(Directory.Exists(path), $"{path} was left behind");
        Assert.Empty(await CopiesOfAsync(harness, repository.Path));

        // A stop while the legs run: the step is cancelled, and its copy goes all the same.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var stopped = new FakeLegs
        {
            WhileItRuns = _ =>
            {
                stop.Cancel();
                throw new OperationCanceledException(stop.Token);
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SweepStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, stopped, new TestIsolation(), harness.WorktreeService, harness.GraphSyncs)
            .RunAsync(Run(step, repository.Path, instance: "rounds#1.items[0].item-arms"), stop.Token));

        Assert.Single(stopped.Asked);
        Assert.False(Directory.Exists(path), $"{path} was left behind by the stop");
        Assert.Empty(await CopiesOfAsync(harness, repository.Path));
    }

    /// <summary>
    /// A sweep whose arms expression yielded none - the item's record arms nothing - sweeps nothing and is done, saying
    /// so: no copy is made and no leg is asked, for an empty list is never every arm. A sweep that names no arms at all
    /// still sweeps every arm.
    /// </summary>
    [Fact]
    public async Task ASweepWhoseArmsYieldedNone_SweepsNothing_AndIsDone()
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: false));
        var legs = new FakeLegs();
        var runner = new SweepStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.WorktreeService, harness.GraphSyncs);

        var none = await runner.RunAsync(
            Run(
                new WorkflowStep { Name = "item-arms", Type = "check-mutations", Base = SweepStepRunner.ItemBase, Pending = true, Arms = new FlowList([], Expression: "implement.latest.arms") },
                repository.Path,
                read: new Dictionary<string, FlowValue> { ["arms"] = FlowValue.Of(new JsonArray()) },
                instance: "rounds#1.items[0].item-arms"),
            Token);

        Assert.Equal((true, HarnessExit.Success), (none.Passed, none.Exit));
        Assert.Equal(
            """{"passed":true,"exitCode":0,"verdicts":[],"red":[],"runDirectory":null,"swept":"no arm: its arms 'implement.latest.arms' yielded none, so nothing was swept"}""",
            none.Output!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Empty(legs.Asked);
        Assert.Empty(await CopiesOfAsync(harness, repository.Path));

        var every = await runner.RunAsync(Run(new WorkflowStep { Name = "sweep", Type = "check-mutations" }, repository.Path), Token);

        Assert.True(every.Passed, every.Error);
        Assert.Null(Assert.IsType<MutationLegsRequest>(Assert.Single(legs.Asked)).ArmNames);
    }

    /// <summary>The worktrees git records of <paramref name="repository"/> besides its main checkout.</summary>
    private static async Task<IReadOnlyList<string>> CopiesOfAsync(HarnessFactory harness, string repository)
        => [.. (await harness.GitClient.ListWorktreesAsync(repository, Token)).Where(worktree => !worktree.IsMain).Select(worktree => worktree.Path)];

    /// <summary>
    /// A leg run something other than its legs ended - refused part way, or stopped - ends the step with the run's own
    /// code and line, whatever its reached legs say, and the output it reached is kept with it, passed false.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ALegRunSomethingElseEnded_EndsTheStepWithItsOwnCode(bool stopped, bool cancelled)
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: false));
        var legs = new FakeLegs
        {
            Answer = Result([new LegEntry { Leg = "native", Verdict = LegVerdict.Passed }], HarnessExit.ConfigInvalid, "the configuration changed under the run") with
            {
                Stopped = stopped,
                Cancelled = cancelled,
                Cause = OutcomeCause.None,
            },
        };
        var runner = new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.GraphSyncs);

        var result = await runner.RunAsync(Run(new WorkflowStep { Name = "build", Type = "build" }, repository.Path), Token);

        Assert.False(result.Passed);
        Assert.Equal(HarnessExit.ConfigInvalid, result.Exit);
        Assert.Equal("the configuration changed under the run", result.Error);
        Assert.Empty(result.Legs);
        Assert.False(result.Output!["passed"]!.GetValue<bool>());
        Assert.Equal(HarnessExit.ConfigInvalid, result.Output!["exitCode"]!.GetValue<int>());
    }

    /// <summary>
    /// A leg run its legs stopped - none could run, another run owns a leg's records - is read by its legs' verdicts, as
    /// any other is: the step says each leg and the worst of them, not the run's own code. One refused part way, or
    /// stopped with a leg it placed left with no line, ends the step with the run's own code and no leg.
    /// </summary>
    [Theory]
    [InlineData("none could run", LegVerdict.SkippedUnavailable, LegsExit.Unavailable, "", true)]
    [InlineData("its records are another run's", LegVerdict.LogHeld, LegExit.LogHeld, "", true)]
    [InlineData("refused part way", LegVerdict.Passed, HarnessExit.Refused, "native", false)]
    [InlineData("a placed leg left with no line", LegVerdict.Passed, HarnessExit.CommandFailed, "native other", false)]
    public async Task ALegRunItsLegsStopped_IsReadByTheirVerdicts(string why, LegVerdict verdict, int exit, string placed, bool readByLegs)
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: false));
        var legs = new FakeLegs
        {
            Answer = Result([new LegEntry { Leg = "native", Verdict = verdict }], exit, why) with { Stopped = true },
            WhileItRuns = watch =>
            {
                if (placed.Length > 0)
                {
                    watch.Placed([.. placed.Split(' ').Select(leg => new LegPlace(leg, InACopy: false))]);
                }
            },
        };
        var runner = new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs, new TestIsolation(), harness.GraphSyncs);

        var result = await runner.RunAsync(Run(new WorkflowStep { Name = "build", Type = "build" }, repository.Path), Token);

        Assert.False(result.Passed);

        if (readByLegs)
        {
            Assert.Equal([new FlowLeg("native", verdict)], result.Legs);
            Assert.Equal(FlowLegVerdicts.CodeOf(verdict), result.Exit);
        }
        else
        {
            Assert.Empty(result.Legs);
            Assert.Equal((exit, why), (result.Exit, result.Error));
        }
    }

    /// <summary>
    /// What a leg step answers: each leg's own verdict; red holding the red legs alone - a leg that failed, with the phase
    /// that did not pass, the ledger's line and, in tail, what it printed that its digest does not say, and the units its build failed as the ledger's
    /// --json says them - and not one incomplete or passed; of a sweep,
    /// each arm that is not as its registry says, naming it, and a leg that failed outside every arm with no arm; passed
    /// only where every leg passed; and no run directory where the run made none.
    /// </summary>
    [Fact]
    public void TheOutput_HoldsEachLegsVerdict_AndTheRedLegsAlone()
    {
        LegEntry[] entries =
        [
            new()
            {
                Leg = "broken",
                Verdict = LegVerdict.Failed,
                Detail = "build failed",
                TailPhase = "build",
                LogTail = ["error: x"],
                FailedUnits = new FailedUnits("ninja", [new FailedUnit("src/a.c", "CMakeFiles/app.dir/a.c.o")], null, KeptGoing: true, StopsAtFirst: true, 2, 3),
            },
            new() { Leg = "moved", Verdict = LegVerdict.InputsMoved, Detail = "a file moved" },
            new() { Leg = "fine", Verdict = LegVerdict.Passed },
            new()
            {
                Leg = "swept",
                Verdict = LegVerdict.Survived,
                Detail = "1 survived",
                Arms =
                [
                    new ArmEntry { Arm = "A-ONE", Verdict = LegVerdict.Survived, Detail = "no test failed" },
                    new ArmEntry { Arm = "A-TWO", Verdict = LegVerdict.Passed },
                ],
            },
            new()
            {
                Leg = "both",
                Verdict = LegVerdict.Failed,
                Detail = "the clean build failed",
                TailPhase = "build",
                Arms = [new ArmEntry { Arm = "A-THREE", Verdict = LegVerdict.Survived, Detail = "no test failed" }],
            },
        ];

        var legs = LegStepRunner.Output(Result(entries[..3], HarnessExit.CommandFailed, "a leg failed"), arms: false);

        Assert.Equal(
            """{"passed":false,"exitCode":20,"verdicts":[{"leg":"broken","verdict":"failed"},{"leg":"moved","verdict":"inputs-moved"},{"leg":"fine","verdict":"passed"}],"red":[{"leg":"broken","verdict":"failed","phase":"build","detail":"build failed","tail":["error: x"],"digest":null,"log":null,"failedUnits":{"tool":"ninja","count":1,"units":[{"source":"src/a.c","step":"CMakeFiles/app.dir/a.c.o"}],"keptGoing":true,"stopsAtFirst":true,"stepsRun":2,"stepsPlanned":3,"said":"1 unit(s) failed to build; ninja went on past them and ran 2 of the 3 steps it had to run: the one it did not run needs one that failed"}}],"runDirectory":"/runs/r"}""",
            legs.ToJsonString());

        var sweep = LegStepRunner.Output(Result([entries[0], entries[3], entries[4]], LegExit.Survived, "an arm survived") with { RunDirectory = null }, arms: true);

        // A sweep's leg red by an arm says the arm alone; one that also failed outside every arm says both.
        Assert.Equal(
            """[{"leg":"broken","verdict":"failed","phase":"build","detail":"build failed","tail":["error: x"],"digest":null,"log":null,"arm":null},{"leg":"swept","verdict":"survived","phase":null,"detail":"no test failed","tail":[],"digest":null,"log":null,"arm":"A-ONE"},"""
                + """{"leg":"both","verdict":"survived","phase":null,"detail":"no test failed","tail":[],"digest":null,"log":null,"arm":"A-THREE"},{"leg":"both","verdict":"failed","phase":"build","detail":"the clean build failed","tail":[],"digest":null,"log":null,"arm":null}]""",
            sweep["red"]!.ToJsonString());
        Assert.Null(sweep["runDirectory"]);

        Assert.True(LegStepRunner.Output(Result([entries[2]], HarnessExit.Success, "passed"), arms: false)["passed"]!.GetValue<bool>());
        Assert.False(LegStepRunner.Output(Result([], HarnessExit.Success, "nothing"), arms: false)["passed"]!.GetValue<bool>());
        // Each holds to its type's schema, as the run's records validate it.
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("build")!.Output!)!, legs));
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("check-mutations")!.Output!)!, sweep));
    }

    /// <summary>
    /// A red entry whose leg holds a digest renders it for a model: the first error said whole with its stack rendered
    /// to its frames in the tree, the next distinct to four as one line each, the rest counted by code and by file - and
    /// every line reaches a model once: the digest holds an error, so it says the lines no parser held and the tail is
    /// empty, never repeating them.
    /// </summary>
    [Fact]
    public void ARedEntryHoldingADigest_RendersItForAModel_EachLineOnce()
    {
        var digest = ResultDigest.Of(
            [
                new Diagnostic(
                    "msbuild",
                    "error",
                    "CS0103",
                    "The name 'x' does not exist in the current context",
                    "src/Program.cs",
                    12,
                    35,
                    Project: "src/App.csproj",
                    Stack:
                    [
                        new StackFrame("   at App.Program.Main() in src/Program.cs:line 5", "src/Program.cs", 5, Symbol: "App.Program.Main", InTree: true),
                        new StackFrame("   at System.Lazy.SetValue()", Module: "System.Private.CoreLib"),
                        new StackFrame("   at System.Lazy.CreateValue()", Module: "System.Private.CoreLib"),
                        new StackFrame("   at System.Lazy.Create()", Module: "System.Private.CoreLib"),
                        new StackFrame("   at App.Program.Run() in src/Program.cs:line 20", "src/Program.cs", 20, Symbol: "App.Program.Run", InTree: true),
                    ]),
                new Diagnostic("msbuild", "error", "CS0103", "The name 'x' does not exist in the current context", "src/Program.cs", 12, 35, Project: "src/App.iOS.csproj"),
                new Diagnostic("msbuild", "error", "CS1002", "; expected", "src/Other.cs", 40, 1),
            ],
            [],
            ["a stray line nobody parsed"]);

        var legs = LegStepRunner.Output(
            Result([new LegEntry { Leg = "broken", Verdict = LegVerdict.Failed, Detail = "build failed", TailPhase = "build", LogTail = ["error: x"], Digest = digest }], HarnessExit.CommandFailed, "a leg failed"),
            arms: false);

        var red = legs["red"]!.AsArray().Single()!.AsObject();

        Assert.Empty(red["tail"]!.AsArray());

        var model = red["digest"]!.AsObject();

        Assert.Equal(["a stray line nobody parsed"], model["unparsed"]!.AsArray().Select(line => line!.GetValue<string>()));
        Assert.False(model.ContainsKey("unparsedLeftOut"));
        Assert.Equal(2, model["errors"]!.GetValue<int>());

        var first = red["digest"]!["first"]!.AsObject();

        Assert.Equal("msbuild CS0103 src/Program.cs(12,35): The name 'x' does not exist in the current context (printed 2 times)", first["said"]!.GetValue<string>());
        Assert.Equal(2, first["count"]!.GetValue<int>());

        // The stack as a model reads it: every frame in the tree, the crossing frame on each side, and the run between left out.
        Assert.Equal(
            [
                "at App.Program.Main() in src/Program.cs:line 5",
                "at System.Lazy.SetValue()",
                "... 1 frames in System.Private.CoreLib",
                "at System.Lazy.Create()",
                "at App.Program.Run() in src/Program.cs:line 20",
            ],
            first["stack"]!.AsArray().Select(line => line!.GetValue<string>()));

        var more = red["digest"]!["diagnostics"]!.AsArray().Single()!.AsObject();

        Assert.Equal("msbuild CS1002 src/Other.cs(40,1): ; expected", more["said"]!.GetValue<string>());

        // The four say every printing, so nothing is left to count by code or by file, and a model is handed no count of it.
        Assert.False(model.ContainsKey("byCode"));
        Assert.False(model.ContainsKey("byFile"));

        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("build")!.Output!)!, legs));
    }

    /// <summary>Makes agent a1 of the run as its create-agent step does, and answers the run's layout and the agent's worktree.</summary>
    private static async Task<(OrchestratorLayout Layout, string Worktree)> AgentAsync(HarnessFactory harness, TempDirectory repository)
    {
        var layout = await LayoutAsync(harness, repository);
        harness.FileSystem.CreateDirectory(layout.Directory);
        harness.OrchestrationStore.WriteOrchestrator(layout, new OrchestratorRecord { Kind = OrchestratorRecord.KindName, Name = "r1", Model = "flow-engine", Parallel = 2, CreatedAt = DateTimeOffset.UnixEpoch });
        var made = await new CreateAgentRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.AgentService)
            .RunAsync(Run(new WorkflowStep { Name = "make", Type = "create-agent", Agent = "worker.md" }, repository.Path) with { Made = new FlowMadeAgent("a1", "model-one") }, Token);
        Assert.True(made.Passed, made.Error);

        return (layout, harness.OrchestrationStore.ReadAgent(layout, "a1")!.WorktreePath((await harness.ContextLoader.LoadAsync(repository.Path, Token)).Layout)!);
    }

    private static LegRunResult Result(IReadOnlyList<LegEntry> entries, int exit, string message)
        => new("build", RunId.New(), "/runs/r", LedgerReport.From(entries, 0), exit, message, Cancelled: false, Unfinished: [], Details: []);

    /// <summary>Every leg use case at once: keeps each request it is asked, and answers each with what the test gives it.</summary>
    private sealed class FakeLegs : IBuildLegs, ITestLegs, IRunnerLegs, IMutationLegs
    {
        /// <summary>Each request asked, in order.</summary>
        public List<LegCommandRequest> Asked { get; } = [];

        /// <summary>What every run answers.</summary>
        public LegRunResult Answer { get; init; } = Result([new LegEntry { Leg = "native", Verdict = LegVerdict.Passed }], HarnessExit.Success, "passed");

        /// <summary>What the leg service says to the request's watch while the run goes.</summary>
        public Action<ILegRunWatch>? WhileItRuns { get; init; }

        /// <summary>The process scope the last run ran under; null where it ran under none.</summary>
        public ProcessScope? Scope { get; private set; }

        public Task<LegRunResult> RunAsync(BuildLegsRequest request, RunId run, CancellationToken cancellationToken = default) => Answered(request);

        public Task<LegRunResult> RunAsync(TestLegsRequest request, RunId run, CancellationToken cancellationToken = default) => Answered(request);

        public Task<LegRunResult> RunAsync(RunnerLegsRequest request, RunId run, CancellationToken cancellationToken = default) => Answered(request);

        public Task<LegRunResult> RunAsync(MutationLegsRequest request, RunId run, CancellationToken cancellationToken = default) => Answered(request);

        public Task<UseCaseResult<ArmListing>> ListAsync(MutationLegsRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("a step never lists arms");

        public Task<LegRunResult> CheckBeforeAdmissionAsync(RunnerLegsRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("a step never checks a run's legs alone: only a WSL distribution is asked to");

        private Task<LegRunResult> Answered(LegCommandRequest request)
        {
            Asked.Add(request);
            Scope = ProcessScope.Current;
            WhileItRuns?.Invoke(request.Watch!);
            return Task.FromResult(Answer);
        }
    }
}
