using System.Text.Json.Nodes;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// One unfinished run at a time: the repository's one section holds the scan for unfinished runs, the r&lt;N&gt;
/// allocation and the run record's creation (a name that is taken refused, naming its record and the way on with that
/// one), with the run's first status written there; the test is made again at the start of every session, leaving out
/// the run itself; and the path budget at a run's start counts each copy and worker its work keeps inside its trees.
/// </summary>
public sealed class FlowSessionsTests
{
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    private static WorkflowDefinition Definition()
        => Parse("""
            name: flow
            steps:
              - name: one
                type: orient
              - name: two
                type: orient
                dependsOn: [one]
            """);

    /// <summary>The run's owner, released as the session that made the run releases it at its end.</summary>
    private static void Released(HarnessFactory harness, OrchestratorLayout run)
        => new FlowRunOwner(harness.OrchestrationStore, harness.Identity).Release(run);

    /// <summary>
    /// A run starts with one more than the highest r&lt;N&gt;, its record written, its first status - every step of the
    /// definition NotStarted, the run Running - and its owner: this process, claimed before the section is left.
    /// </summary>
    [Fact]
    public void ARunStartsWithTheNextName_ItsRecordItsFirstStatusAndItsOwner()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);

        var (run, context) = sessions.Create(layout, Definition());

        Assert.Equal("r1", run.Name);
        Assert.Equal("r1", context.Run);
        Assert.Equal(Environment.ProcessId, harness.OrchestrationStore.ReadOwner(run)!.ProcessId);

        var record = harness.OrchestrationStore.ReadOrchestrator(run);

        Assert.NotNull(record);
        Assert.Equal("flow-engine", record.Model);
        Assert.Equal(2, record.Parallel);

        var status = harness.OrchestrationStore.ReadWorkflowStatus(run);

        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
        Assert.Equal(["one", "two"], status.Steps.Keys.Order(StringComparer.Ordinal));
        Assert.All(status.Steps.Values, step => Assert.Equal("NotStarted", step.Status));

        // The next run takes one more than the highest name there, whatever lies between.
        Released(harness, run);
        Directory.CreateDirectory(Path.Combine(layout.OrchestratorsDirectory, "r7"));
        Directory.Delete(run.Directory, recursive: true);

        Assert.Equal("r8", sessions.Create(layout, Definition()).Run.Name);
    }

    /// <summary>A <c>--run</c> name that is taken is refused, naming its record and the way on with that one, by the one sentence create-orchestrator says it with.</summary>
    [Fact]
    public void ARunNameThatIsTakenIsRefused_NamingItsRecordAndTheWayOn()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);

        var (mine, _) = sessions.Create(layout, Definition(), wantedName: "mine");
        Released(harness, mine);

        var raised = Assert.Throws<HarnessException>(() => sessions.Create(layout, Definition(), wantedName: "mine"));

        Assert.Equal(HarnessExit.Refused, raised.ExitCode);
        Assert.Equal(OrchestrationReports.RunExists(mine), raised.Message);
        Assert.Equal($"run 'mine' exists - '{mine.RecordFile}' is its record; --run names a new run, and resume-orchestrator mine goes on with that one. Nothing was changed.", raised.Message);
    }

    /// <summary>A <c>--run</c> name whose directory is there holding no run's record is refused: a run is never made over what something else left there.</summary>
    [Fact]
    public void ARunNameWhoseDirectoryHoldsNoRecordIsRefused()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);
        var left = OrchestratorLayout.Of(layout, "left");

        Directory.CreateDirectory(left.Directory);
        File.WriteAllText(left.DefinitionFile, "{}");

        var raised = Assert.Throws<HarnessException>(() => sessions.Create(layout, Definition(), wantedName: "left"));

        Assert.Equal(HarnessExit.Refused, raised.ExitCode);
        Assert.Equal(
            $"'{left.Directory}' is there and holds no run's record ('{left.RecordFile}'); --run names a new run, and a run is never made over what something else left there. "
            + "Nothing was changed: remove it, or name the run otherwise.",
            raised.Message);
        Assert.Equal("{}", File.ReadAllText(left.DefinitionFile));
    }

    /// <summary>A start that its refusal check refuses - its path budget - writes nothing: the name is chosen, and nothing of a run is made by it.</summary>
    [Fact]
    public void AStartItsCheckRefuses_WritesNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);

        var raised = Assert.Throws<HarnessException>(() => sessions.Create(layout, Definition(), refusal: name => $"run '{name}' is too deep"));

        Assert.Equal("run 'r1' is too deep", raised.Message);
        Assert.False(Directory.Exists(OrchestratorLayout.Of(layout, "r1").Directory));
        Assert.False(File.Exists(OrchestratorLayout.Of(layout, "r1").OwnerFile));
    }

    /// <summary>A session is refused while another run of the repository is unfinished over a live owner, and the refusal names the run and the ways on with it.</summary>
    [Fact]
    public void ASessionIsRefusedWhileAnotherRuns_OverALiveOwner()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);

        // The run's engine holds it: its owner was claimed with it.
        _ = sessions.Create(layout, Definition());

        var raised = Assert.Throws<HarnessException>(() => sessions.Create(layout, Definition(), wantedName: "second"));

        Assert.Equal(HarnessExit.Refused, raised.ExitCode);
        Assert.Contains("run 'r1' of this repository is unfinished (running; owned by a live engine)", raised.Message, StringComparison.Ordinal);
        Assert.Contains("resume-orchestrator r1 or answer-orchestrator r1 goes on with it, delete-orchestrator r1 ends it", raised.Message, StringComparison.Ordinal);
    }

    /// <summary>A paused run whose agents still hold worktrees is unfinished; one whose agents hold none is not - an agent with no worktree has no work in the tree.</summary>
    [Fact]
    public async Task APausedRunIsUnfinishedOnlyWhereItsAgentsHoldWorktrees()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);
        var definition = Definition();

        var (other, context) = sessions.Create(layout, definition);

        // Its session has ended: nothing holds it but its records.
        Released(harness, other);

        // The run pauses with an agent still open: its state file says Paused, and its agent holds a worktree.
        using (var records = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, other, context))
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new Facts(), records: records);
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);

            harness.OrchestrationStore.WriteAgent(
                other,
                new AgentRecord
                {
                    Kind = AgentRecord.KindName,
                    Name = "a1",
                    Orchestrator = other.Name,
                    Model = "m",
                    CreatedAt = DateTimeOffset.UtcNow,
                    State = AgentStates.Live,
                    Worktree = "r1/a1",
                    WorktreesRoot = ".harness-config/worktrees",
                });
        }

        // The run's own status says Paused - written by hand as a pause would have, for the test is of the scan, not of the pause.
        var flow = harness.OrchestrationStore.ReadFlow(other)!;
        harness.OrchestrationStore.WriteFlow(other, flow with { Status = "Paused" });

        var held = Assert.Throws<HarnessException>(() => sessions.Create(layout, definition, wantedName: "second"));

        Assert.Contains("paused; 1 agent with worktree still open", held.Message, StringComparison.Ordinal);

        // The agent closes its worktree: the run holds nothing in the tree, and a second run may start.
        var agent = harness.OrchestrationStore.ReadAgent(other, "a1")!;
        harness.OrchestrationStore.WriteAgent(other, agent with { Access = AgentRecord.AccessRead, Worktree = null, WorktreesRoot = null });

        var second = sessions.Create(layout, definition, wantedName: "second");

        Assert.Equal("second", second.Run.Name);
    }

    /// <summary>
    /// The test is made at the start of every session - a resume, an answer alike - and leaves out the run the session
    /// is for; the session's owner is claimed in the same section, and a stop asked of an engine that ended before it
    /// took it is taken back, so it reaches no later session.
    /// </summary>
    [Fact]
    public void TheTestIsMadeAtTheStartOfEverySession_LeavingOutTheRunItself()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);

        var (first, _) = sessions.Create(layout, Definition());
        Released(harness, first);
        var (second, _) = sessions.Create(layout, Definition());
        Released(harness, second);

        // While the first runs, a session of the second is refused.
        _ = new FlowRunOwner(harness.OrchestrationStore, harness.Identity).Claim(first);

        var raised = Assert.Throws<HarnessException>(() => sessions.Claim(layout, second));

        Assert.Contains($"run '{first.Name}' of this repository is unfinished", raised.Message, StringComparison.Ordinal);
        Assert.Null(harness.OrchestrationStore.ReadOwner(second));

        // Once its engine has left, a session of the first is not refused - the test leaves out the run it is for - and
        // a stop asked of the engine that left reaches this session no more.
        Released(harness, first);
        harness.OrchestrationStore.WriteStopFile(first, new FlowStopFile(1, Stop: new FlowStopStop(DateTimeOffset.UtcNow)));

        Assert.Null(sessions.Claim(layout, first));
        Assert.Equal(Environment.ProcessId, harness.OrchestrationStore.ReadOwner(first)!.ProcessId);
        Assert.Null(harness.OrchestrationStore.ReadStopFile(first));
    }

    /// <summary>
    /// What another run holds that cannot be told - an agent whose record does not read - refuses a session as work in
    /// the tree does, by a sentence that says so: it is never read as nothing.
    /// </summary>
    [Fact]
    public async Task AnotherRunsUnreadableAgent_RefusesBySayingSo()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);
        var definition = Definition();

        var (other, context) = sessions.Create(layout, definition);
        Released(harness, other);

        using (var records = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, other, context))
        {
            _ = await new FlowCoordinator(definition, new Runner(), new Facts(), records: records).RunAsync(TestContext.Current.CancellationToken);
        }

        harness.OrchestrationStore.WriteFlow(other, harness.OrchestrationStore.ReadFlow(other)! with { Status = "Paused" });
        Directory.CreateDirectory(other.AgentDirectory("a1"));
        File.WriteAllText(other.AgentRecordFile("a1"), "not a record");

        var raised = Assert.Throws<HarnessException>(() => sessions.Create(layout, definition, wantedName: "second"));

        Assert.StartsWith("run 'r1' of this repository is unfinished (paused, and whether its agent 'a1' holds a worktree cannot be told: ", raised.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two starts at once on two threads - the section is a machine-wide one, which lets the same thread in twice - make
    /// one run: the other is refused, naming it.
    /// </summary>
    [Fact]
    public void TwoStartsAtOnce_OneIsRefusedNamingTheOther()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);
        var results = AtOnce(
            () => sessions.Create(layout, Definition(), wantedName: "left").Run.Name,
            () => sessions.Create(layout, Definition(), wantedName: "right").Run.Name);

        var made = Assert.Single(results, result => result.Made is not null).Made!;
        var refused = Assert.Single(results, result => result.Refused is not null).Refused!;

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith($"run '{made}' of this repository is unfinished (running; owned by a live engine)", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Two sessions of one run at once, on two threads, claim it once: the other is refused by the live owner the first wrote.</summary>
    [Fact]
    public void TwoResumesAtOnce_OnlyOneClaimsTheRun()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);
        var (run, _) = sessions.Create(layout, Definition());
        Released(harness, run);

        var results = AtOnce(
            () => sessions.Claim(layout, run) ?? "claimed",
            () => sessions.Claim(layout, run) ?? "claimed");

        Assert.Single(results, result => result.Made == "claimed");
        Assert.StartsWith("run 'r1' is owned by a live engine (process ", Assert.Single(results, result => result.Refused is not null).Refused!.Message, StringComparison.Ordinal);
    }

    /// <summary>Runs two pieces of work on two threads of their own, let go at one moment, and says what each made or why it was refused.</summary>
    private static IReadOnlyList<(string? Made, HarnessException? Refused)> AtOnce(Func<string> left, Func<string> right)
    {
        using var barrier = new Barrier(2);
        var results = new (string? Made, HarnessException? Refused)[2];
        var works = new[] { left, right };
        var threads = Enumerable.Range(0, 2).Select(at => new Thread(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(30));

            try
            {
                results[at] = (works[at](), null);
            }
            catch (HarnessException ex)
            {
                results[at] = (null, ex);
            }
        })).ToList();

        threads.ForEach(thread => thread.Start());
        Assert.All(threads, thread => Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "a thread did not end within a minute"));

        return results;
    }

    /// <summary>
    /// The path budget at a run's start counts each copy and worker its work keeps inside its deepest tree, as a copy's
    /// own check counts it - its build below it, once - and the longest of them is what it is held to.
    /// </summary>
    [Fact]
    public void ThePathBudgetAtAStartCountsEachCopyAndWorker_AsACopysOwnCheckDoes()
    {
        var budget = new PathBudget(new HostPlatform());
        var settings = new WorktreeSettings { PathLimit = 600 };
        var root = Path.Combine(Path.GetTempPath(), "trees", "r1");
        var tree = Path.Combine(root, "a99");
        var plain = new VariantKey("x64", "gcc", "Debug", null);
        var longer = new VariantKey("x64", "gcc-with-a-much-longer-toolchain-name", "RelWithDebInfo", "address");

        var copied = FlowRunPaths.Check(budget, settings, root, "a99", [plain], []);
        var copy = DssHarness.Core.Legs.LegCopies.PathOf(tree, plain);

        Assert.True(copied.IsWithinBudget);
        Assert.Equal(DssHarness.Core.Legs.CopyPathBudget.Check(budget, settings, copy, plain, settings.PathBudgetReserve).RequiredLength, copied.RequiredLength);

        // A worker is reckoned beside the copy, and the longest variant is what the run is held to.
        var swept = FlowRunPaths.Check(budget, settings, root, "a99", [plain], [plain]);
        var worker = DssHarness.Core.Mutations.MutationWorkers.PathOf(tree, plain, 1);

        Assert.Equal(Math.Max(copied.RequiredLength, DssHarness.Core.Legs.CopyPathBudget.Check(budget, settings, worker, plain, settings.PathBudgetReserve).RequiredLength), swept.RequiredLength);
        Assert.True(FlowRunPaths.Check(budget, settings, root, "a99", [plain, longer], []).RequiredLength > copied.RequiredLength, "the longer variant is the one reckoned");

        var over = FlowRunPaths.Check(budget, new WorktreeSettings { PathLimit = copied.RequiredLength }, root, "a99", [plain], []);

        Assert.False(over.IsWithinBudget);
    }

    /// <summary>
    /// A run's start is held to the path budget of the legs this machine runs that its leg steps may name, under its
    /// deepest agent's worktree: refused by a sentence naming that worktree where it passes the limit. A leg that works
    /// in its tree rather than a copy keeps no copy to count, and a file a work item writes is counted where it lies.
    /// </summary>
    [Fact]
    public void ARunsStartIsHeldToThePathBudgetOfItsLegSteps()
    {
        var platform = new HostPlatform();
        var layout = new HarnessLayout(Path.Combine(Path.GetTempPath(), "repo"), Path.Combine(Path.GetTempPath(), "repo"));
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: build
            """);
        // Its os written in another case is this machine's all the same, as the configuration's check reads it.
        var leg = new LegConfig { Os = platform.PlatformKey.ToUpperInvariant(), Processor = "x64", Toolchain = "gcc", Config = "Debug", LocalCopy = true };
        HarnessConfig Limited(int limit) => new() { Legs = new Dictionary<string, LegConfig>(StringComparer.Ordinal) { ["local"] = leg }, Worktrees = new WorktreeSettings { PathLimit = limit } };
        var config = Limited(40);

        var refusal = FlowRunPaths.RefusalAtStart(new PathBudget(platform), new HarnessContext(layout, config), "r1", definition, platform.PlatformKey);
        var (inCopy, tree) = FlowRunPaths.AtStart(new PathBudget(platform), new HarnessContext(layout, config), "r1", definition, platform.PlatformKey);

        Assert.NotNull(refusal);
        Assert.StartsWith("run 'r1' of 'flow' would work in paths too long for this machine - the deepest of its agents' worktrees with the copies and workers its leg steps keep inside it: ", refusal, StringComparison.Ordinal);
        Assert.EndsWith(" Nothing was made.", refusal, StringComparison.Ordinal);
        Assert.Contains($"'{tree}'", refusal, StringComparison.Ordinal);
        Assert.Null(FlowRunPaths.RefusalAtStart(new PathBudget(platform), new HarnessContext(layout, Limited(2000)), "r1", definition, platform.PlatformKey));

        var inTree = Limited(40);
        inTree.Legs["local"] = new LegConfig { Os = platform.PlatformKey, Processor = "x64", Toolchain = "gcc", Config = "Debug" };
        var (alone, _) = FlowRunPaths.AtStart(new PathBudget(platform), new HarnessContext(layout, inTree), "r1", definition, platform.PlatformKey);

        Assert.True(alone.RequiredLength < inCopy.RequiredLength, "a leg working in its tree keeps no copy to count");
        Assert.Equal(Path.TrimEndingDirectorySeparator(tree).Length + inTree.Worktrees.PathBudgetMargin, alone.RequiredLength);

        // A file a work item writes is counted as it lies below the tree: its path after one separator.
        var (written, _) = FlowRunPaths.AtStart(new PathBudget(platform), new HarnessContext(layout, inTree), "r1", definition, platform.PlatformKey, written: 10);
        Assert.Equal(Path.TrimEndingDirectorySeparator(tree).Length + 1 + 10 + inTree.Worktrees.PathBudgetMargin, written.RequiredLength);
    }

    /// <summary>A runner that answers every step as asked.</summary>
    private sealed class Runner : IFlowStepRunner
    {
        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult(FlowStepResult.Ended(passed: true, 0));

        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>The facts every run of these tests asks of its runners.</summary>
    private sealed class Facts : IFlowRunnerFacts
    {
        public bool WritesTree(string runner) => false;
    }
}
