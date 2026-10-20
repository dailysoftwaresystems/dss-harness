using System.Text.Json.Nodes;
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

    /// <summary>A run starts with the first free name, its record written, and its first status: every step of the definition NotStarted, the run Running.</summary>
    [Fact]
    public void ARunStartsWithTheFirstFreeName_ItsRecordAndItsFirstStatus()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);

        var (run, context) = sessions.Create(layout, Definition());

        Assert.Equal("r1", run.Name);
        Assert.Equal("r1", context.Run);

        var record = harness.OrchestrationStore.ReadOrchestrator(run);

        Assert.NotNull(record);
        Assert.Equal("flow-engine", record.Model);
        Assert.Equal(2, record.Parallel);

        var status = harness.OrchestrationStore.ReadWorkflowStatus(run);

        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
        Assert.Equal(["one", "two"], status.Steps.Keys.Order(StringComparer.Ordinal));
        Assert.All(status.Steps.Values, step => Assert.Equal("NotStarted", step.Status));

        // The next run takes the next name, and the one after the next: a deleted run's name is never reused.
        Assert.Equal("r2", sessions.Create(layout, Definition()).Run.Name);
    }

    /// <summary>A <c>--run</c> name that is taken is refused, naming its record and the way on with that one.</summary>
    [Fact]
    public void ARunNameThatIsTakenIsRefused_NamingItsRecordAndTheWayOn()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);

        _ = sessions.Create(layout, Definition(), wantedName: "mine");

        var raised = Assert.Throws<HarnessException>(() => sessions.Create(layout, Definition(), wantedName: "mine"));

        Assert.Equal(HarnessExit.Refused, raised.ExitCode);
        Assert.Contains("run 'mine' exists; --run names a new run, and resume-orchestrator mine goes on with that one", raised.Message, StringComparison.Ordinal);
    }

    /// <summary>A session is refused while another run of the repository is unfinished over a live owner, and the refusal names the run and the ways on with it.</summary>
    [Fact]
    public void ASessionIsRefusedWhileAnotherRuns_OverALiveOwner()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);

        var (other, _) = sessions.Create(layout, Definition());

        _ = new FlowRunOwner(harness.OrchestrationStore, harness.Identity).Claim(other);

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

    /// <summary>The test is made at the start of every session - a resume, an answer alike - and leaves out the run the session is for.</summary>
    [Fact]
    public void TheTestIsMadeAtTheStartOfEverySession_LeavingOutTheRunItself()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);

        var (first, _) = sessions.Create(layout, Definition());
        var (second, _) = sessions.Create(layout, Definition());

        // While the first runs, a session of the second is refused - and a session of the first is not, for the test
        // leaves out the run it is for.
        _ = new FlowRunOwner(harness.OrchestrationStore, harness.Identity).Claim(first);

        var raised = Assert.Throws<HarnessException>(() => sessions.Guard(layout, second.Name));

        Assert.Contains($"run '{first.Name}' of this repository is unfinished", raised.Message, StringComparison.Ordinal);

        sessions.Guard(layout, first.Name);
    }

    /// <summary>The path budget at a run's start counts each copy and worker its work keeps inside its trees: the longest of them, checked as a copy's own check checks the build below it.</summary>
    [Fact]
    public void ThePathBudgetAtAStartCountsEachCopyAndWorker()
    {
        var budget = new PathBudget(new HostPlatform());
        var settings = new WorktreeSettings { PathLimit = 200 };
        var within = FlowRunPaths.Check(
            budget,
            settings,
            worktreesRoot: "C:\\trees",
            agentName: "a1",
            legKeys: ["long-leg-key"],
            mutationKeys: ["sweep"],
            reserve: 20);

        Assert.True(within.IsWithinBudget);

        var over = FlowRunPaths.Check(
            budget,
            new WorktreeSettings { PathLimit = within.RequiredLength - 1 },
            worktreesRoot: "C:\\trees",
            agentName: "a1",
            legKeys: ["long-leg-key"],
            mutationKeys: ["sweep"],
            reserve: 20);

        Assert.False(over.IsWithinBudget);

        // A longer mutation key than every leg key is what the budget counts: each is reckoned, the longest wins.
        var byWorker = FlowRunPaths.Check(
            budget,
            settings,
            worktreesRoot: "C:\\trees",
            agentName: "a1",
            legKeys: ["a"],
            mutationKeys: ["a-much-longer-mutation-key"],
            reserve: 20);

        Assert.Equal(
            FlowRunPaths.Check(budget, settings, "C:\\trees", "a1", ["a-much-longer-mutation-key"], ["a-much-longer-mutation-key"], 20).RequiredLength,
            byWorker.RequiredLength);
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
