using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.Application.Legs;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
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
    private static HarnessConfig Config(bool localCopy, string? workflow = null)
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
        };
    }

    private static FlowStepRun Run(WorkflowStep step, string repository, IFlowRunEvents? events = null, string? worksIn = null)
        => new(step, step.Name, 1, FlowValue.Missing, null, null, [], events ?? new Said(), Place: new FlowRunPlace(repository, "r1", null, Flow), WorksIn: worksIn);

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

        public void Used(long tokens, double usd) => Add("used");

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
            Answer = Result([new LegEntry { Leg = "native", Verdict = LegVerdict.Passed }, new LegEntry { Leg = "here", Verdict = LegVerdict.Passed }], HarnessExit.Success, "passed"),
            WhileItRuns = watch =>
            {
                watch.Placed([new LegPlace("native", InACopy: true), new LegPlace("here", InACopy: false)]);
                watch.CopyMade("native");
                watch.Ended("here");
                watch.Ended("native");
            },
        };
        var said = new Said();
        var runner = new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs);

        var result = await runner.RunAsync(Run(new WorkflowStep { Name = "checks", Type = "build" }, repository.Path, said), Token);

        Assert.True(result.Passed, result.Error);
        Assert.Equal(HarnessExit.Success, result.Exit);
        Assert.Equal([new FlowLeg("native", LegVerdict.Passed), new FlowLeg("here", LegVerdict.Passed)], result.Legs);
        Assert.Equal(["placed native in a copy", "placed here in the tree", "every leg placed", "copy of native made", "here ended", "native ended"], said.Events);
        Assert.Equal(
            """{"passed":true,"exitCode":0,"verdicts":[{"leg":"native","verdict":"passed"},{"leg":"here","verdict":"passed"}],"red":[],"runDirectory":"/runs/r"}""",
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
        var runner = new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs);
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
        var runner = new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs);
        var step = new WorkflowStep { Name = "checks", Type = "build", In = "make" };

        var result = await runner.RunAsync(Run(step, repository.Path, worksIn: "a1"), Token);

        Assert.Equal(worktree, legs.Asked.Single().Directory);
        Assert.Equal(["native", "far"], result.Output!["red"]!.AsArray().Select(red => red!["leg"]!.GetValue<string>()));
        var kept = Path.Combine(layout.InstanceDirectory("checks", "a1"), OrchestratorLayout.StepLogsDirectoryName);
        Assert.Equal(["native.test.log"], Directory.EnumerateFiles(kept).Select(Path.GetFileName));
        Assert.Equal("a test failed\nboom\n", File.ReadAllText(Path.Combine(kept, "native.test.log")));
        Assert.True(File.Exists(Path.Combine(layout.InstanceDirectory("checks", "a1"), OrchestratorLayout.StepLedgerFileName)));

        var onMain = await runner.RunAsync(Run(step with { Name = "main-checks", In = null }, repository.Path), Token);

        Assert.Equal(repository.Path, legs.Asked[^1].Directory);
        Assert.Equal(HarnessExit.CommandFailed, onMain.Exit);
        Assert.True(File.Exists(Path.Combine(layout.InstanceDirectory("main-checks", null), OrchestratorLayout.StepLedgerFileName)));
        Assert.False(Directory.Exists(Path.Combine(layout.InstanceDirectory("main-checks", null), OrchestratorLayout.StepLogsDirectoryName)));
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

        var failed = await new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, red)
            .RunAsync(Run(step, repository.Path, worksIn: "a1"), Token);

        Assert.Equal("step (2/2)", failed.Output!["red"]![0]!["phase"]!.GetValue<string>());
        Assert.Equal(["native.step (2-2).log"], Directory.EnumerateFiles(kept).Select(Path.GetFileName));
        Assert.Equal("the second line failed\n", File.ReadAllText(Path.Combine(kept, "native.step (2-2).log")));

        var passed = await new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, new FakeLegs())
            .RunAsync(Run(step, repository.Path, worksIn: "a1"), Token);

        Assert.True(passed.Passed, passed.Error);
        Assert.False(Directory.Exists(kept));
    }

    /// <summary>
    /// A workflow's run step, run by the tool itself: its leg works in a copy of its own and the step's record says the
    /// tree was let go once the copy was made; its output answers the leg's verdict, and its ledger is kept beside it.
    /// </summary>
    [Fact]
    public async Task ARunOfAWorkflow_RunsItsLegStep_AndRecordsTheTreeLetGo()
    {
        var (harness, repository) = await RepositoryAsync(Config(localCopy: true, workflow: "flow"));
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
        Assert.Equal("passed", ledger["legs"]![0]!["verdict"]!.GetValue<string>());
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
        var runner = new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs);
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
    /// for every leg; a test's filter, exclusions, labels and skipBuild; a run's runner, inputs and - for a run that writes
    /// the tree, by its runner or a run check's - in place; a sweep's arms, or none for every arm, and pending.
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
            new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs),
            new TestStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs),
            new RunStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs),
            new SweepStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs),
        ];
        WorkflowStep[] steps =
        [
            new() { Name = "build", Type = "build" },
            new() { Name = "test", Type = "test", Legs = ["native"], Filter = "fast", Exclude = ["slow"], Labels = ["unit"], SkipBuild = true },
            new() { Name = "run", Type = "run", Runner = "probe", Inputs = new Dictionary<string, string> { ["target"] = "all" } },
            new() { Name = "tidy", Type = "run", Runner = "TIDY" },
            new() { Name = "sweep", Type = "check-mutations" },
            new() { Name = "pending", Type = "check-mutations", Arms = ["A-ONE"], Pending = true },
        ];

        foreach (var step in steps)
        {
            var result = await runners.Single(runner => runner.Type == step.Type).RunAsync(Run(step, repository.Path), Token);
            Assert.True(result.Passed, result.Error);
        }

        Assert.Collection(
            legs.Asked,
            asked => Assert.Equal(new BuildLegsRequest { Directory = repository.Path, Watch = asked.Watch }, asked),
            asked => Assert.Equivalent(
                new TestLegsRequest { Directory = repository.Path, LegNames = ["native"], Filter = "fast", Excludes = ["slow"], Labels = ["unit"], SkipBuild = true, Watch = asked.Watch },
                asked,
                strict: true),
            asked => Assert.Equivalent(
                new RunnerLegsRequest { Directory = repository.Path, Runner = "probe", Inputs = new Dictionary<string, string> { ["target"] = "all" }, Watch = asked.Watch },
                asked,
                strict: true),
            asked => Assert.Equivalent(new RunnerLegsRequest { Directory = repository.Path, Runner = "TIDY", InPlace = true, Watch = asked.Watch }, asked, strict: true),
            asked => Assert.Equal(new MutationLegsRequest { Directory = repository.Path, Watch = asked.Watch }, asked),
            asked => Assert.Equivalent(new MutationLegsRequest { Directory = repository.Path, ArmNames = ["A-ONE"], Pending = true, Watch = asked.Watch }, asked, strict: true));
        Assert.All(legs.Asked, asked => Assert.NotNull(asked.Watch));
    }

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
        var runner = new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs);

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
        var runner = new BuildStepRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.FileSystem, harness.ChildSessions, legs);

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
    /// that did not pass, the ledger's line and the last lines it printed - and not one incomplete or passed; of a sweep,
    /// each arm that is not as its registry says, naming it, and a leg that failed outside every arm with no arm; passed
    /// only where every leg passed; and no run directory where the run made none.
    /// </summary>
    [Fact]
    public void TheOutput_HoldsEachLegsVerdict_AndTheRedLegsAlone()
    {
        LegEntry[] entries =
        [
            new() { Leg = "broken", Verdict = LegVerdict.Failed, Detail = "build failed", TailPhase = "build", LogTail = ["error: x"] },
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
            """{"passed":false,"exitCode":20,"verdicts":[{"leg":"broken","verdict":"failed"},{"leg":"moved","verdict":"inputs-moved"},{"leg":"fine","verdict":"passed"}],"red":[{"leg":"broken","verdict":"failed","phase":"build","detail":"build failed","tail":["error: x"]}],"runDirectory":"/runs/r"}""",
            legs.ToJsonString());

        var sweep = LegStepRunner.Output(Result([entries[0], entries[3], entries[4]], LegExit.Survived, "an arm survived") with { RunDirectory = null }, arms: true);

        // A sweep's leg red by an arm says the arm alone; one that also failed outside every arm says both.
        Assert.Equal(
            """[{"leg":"broken","verdict":"failed","phase":"build","detail":"build failed","tail":["error: x"],"arm":null},{"leg":"swept","verdict":"survived","phase":null,"detail":"no test failed","tail":[],"arm":"A-ONE"},"""
                + """{"leg":"both","verdict":"survived","phase":null,"detail":"no test failed","tail":[],"arm":"A-THREE"},{"leg":"both","verdict":"failed","phase":"build","detail":"the clean build failed","tail":[],"arm":null}]""",
            sweep["red"]!.ToJsonString());
        Assert.Null(sweep["runDirectory"]);

        Assert.True(LegStepRunner.Output(Result([entries[2]], HarnessExit.Success, "passed"), arms: false)["passed"]!.GetValue<bool>());
        Assert.False(LegStepRunner.Output(Result([], HarnessExit.Success, "nothing"), arms: false)["passed"]!.GetValue<bool>());

        // Each holds to its type's schema, as the run's records validate it.
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("build")!.Output!)!, legs));
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("check-mutations")!.Output!)!, sweep));
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

        private Task<LegRunResult> Answered(LegCommandRequest request)
        {
            Asked.Add(request);
            Scope = ProcessScope.Current;
            WhileItRuns?.Invoke(request.Watch!);
            return Task.FromResult(Answer);
        }
    }
}
