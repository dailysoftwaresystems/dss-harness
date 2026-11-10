using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.Configuration;
using DssHarness.Core.Repository;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// The fold family as steps of a run - fold, delete-agent, refresh, rebase - over the verbs' typed facts: the dry run made
/// first and read by its standing, never its code; a consent it lacks, paths its within leaves out and conflicting merges
/// alone asked with what they are about, kept in the step's directory; answered, the dry run made again and the consent
/// given by name only where it names what the question showed; the apply held to what the dry run named, a fold's written
/// ahead to the run's ledger of its folds; and what each wrote answered as its output.
/// </summary>
public sealed class FoldStepTests
{
    private const string Agent = "ag";

    private const string RowId = "D-TEST-AGENT-ROW";

    /// <summary>The file both trees change.</summary>
    private const string Both = "src/m.txt";

    private static readonly Dictionary<string, string> Row = new()
    {
        ["status"] = "open",
        ["priority"] = "P2",
        ["trigger"] = "something the agent found",
        ["closing"] = "the fix",
        ["cross-refs"] = "b.txt",
    };

    private static readonly WorkflowDefinition Definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
        name: flow
        steps:
          - name: made
            type: create-agent
            agent: worker.md
          - name: fold
            type: fold
            of: made
        """);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A fold applies what its dry run named, writing its intent to the run's ledger of its folds before the verb is
    /// called and what it left after, and answers what it wrote, removed, the registry files its rows changed and the rows
    /// it applied - its record saying how the apply ended and under which entry of the ledger.
    /// </summary>
    [Fact]
    public async Task AFold_AppliesWhatItsDryRunNamed_ThroughTheLedger_AndAnswersWhatItWrote()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        File.Delete(Path.Combine(worktree, "docs", "x.md"));
        kit.FileRow(Agent, RowId, Row);
        var step = Fold() with { Allow = new FlowAllow([], new FlowList([RowId], null), [], null, []) };

        var folded = await Runner(kit).RunAsync(Run(kit, step), Token);

        Assert.True(folded.Passed, folded.Error);
        Assert.Equal(
            $$"""{"written":["b.txt"],"deleted":["docs/x.md"],"registries":["{{AnchorSettings.DefaultPendingAnchorsPath}}"],"rows":["{{RowId}}"]}""",
            folded.Output!.ToJsonString());

        var ledger = kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!;
        var entry = Assert.Single(ledger.Folds);

        Assert.Equal(("f1", "applied", "fold", Agent), (entry.Id, entry.State, entry.Instance, entry.Agent));
        Assert.Equal(["b.txt", "docs/x.md", AnchorSettings.DefaultPendingAnchorsPath], entry.Paths.Select(path => path.Path));
        Assert.All(entry.Paths, path => Assert.True(path.Changed));
        Assert.Null(entry.Paths[1].Left);
        Assert.Equal([RowId], entry.Rows);

        var record = kit.Harness.OrchestrationStore.ReadFoldRecord(kit.Layout.InstanceDirectory("fold", Agent))!;

        Assert.Equal((0, "f1"), (record.Applied!.ExitCode, record.Applied.Ledger));
        Assert.Null(record.Asked);

        // The run has no graph: none is made.
        Assert.False(File.Exists(kit.Layout.GraphManifestFile));
    }

    /// <summary>
    /// A fold whose dry run would write a path its within leaves out asks foldOutside with that path, kept in its record
    /// with where the answer will stand; answered allow, the dry run is made again and the consent given by name, and it
    /// applies - the registry files its rows change never held to within.
    /// </summary>
    [Fact]
    public async Task AFoldOutsideItsWithin_AsksFoldOutside_AndAnAllowGivesItByName_TheRegistriesNeverHeldToIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(worktree, "c.txt", "new\n");
        kit.FileRow(Agent, RowId, Row);
        var step = Fold() with { Within = new FlowList(["b.txt"], null), Allow = new FlowAllow([], new FlowList([RowId], null), [], null, []) };

        var asked = await Runner(kit).RunAsync(Run(kit, step), Token);

        Assert.Null(asked.Asked);
        Assert.Equal(["c.txt"], asked.DryRun!.Outside);
        Assert.Equal(new FlowFoldQuestion("foldOutside", ["c.txt"]) with { Named = asked.DryRun.Outside }, FlowQuestions.FoldQuestionOf(asked.DryRun));
        Assert.Equal(
            "'fold': the fold of 'ag' would write 'c.txt', which is outside the paths its within names",
            FlowQuestions.FoldProblemOf("fold", asked.DryRun, FlowQuestions.FoldQuestionOf(asked.DryRun)!));

        var record = kit.Harness.OrchestrationStore.ReadFoldRecord(kit.Layout.InstanceDirectory("fold", Agent))!;

        Assert.Equal(("foldOutside", 0), (record.Asked!.Kind, record.Asked.AnswerAt));
        Assert.Equal(["c.txt"], record.Asked.Named);

        var applied = await Runner(kit).RunAsync(Run(kit, step, [new FlowAnswerGiven("q1", "allow")]), Token);

        Assert.True(applied.Passed, applied.Error);
        Assert.Equal(["b.txt", "c.txt"], Texts(applied.Output!["written"]));
        Assert.Equal([AnchorSettings.DefaultPendingAnchorsPath], Texts(applied.Output!["registries"]));

        var granted = Assert.Single(kit.Harness.OrchestrationStore.ReadFoldRecord(kit.Layout.InstanceDirectory("fold", Agent))!.Granted);

        Assert.Equal(new FlowFoldGrant("foldOutside", ["c.txt"], "q1", "allow") with { Named = granted.Named }, granted);
    }

    /// <summary>
    /// A consent the question asked is given by name only where the dry run made again names exactly what it showed: an
    /// agent that filed another row since is asked again, with what its dry run names now, its answer standing after the
    /// first's; and a consent given before the trees moved is never passed.
    /// </summary>
    [Fact]
    public async Task AnAnswerGivenBeforeTheTreesMoved_IsNeverPassed_AndTheQuestionIsAskedAgain()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync(Agent);
        kit.FileRow(Agent, RowId, Row);

        var asked = await Runner(kit).RunAsync(Run(kit, Fold()), Token);

        Assert.Equal("foldNewRows", FlowQuestions.FoldQuestionOf(asked.DryRun!)!.Kind);
        Assert.Equal([RowId], asked.DryRun!.NamedOf(ConsentKind.FoldNewRows));

        kit.FileRow(Agent, "D-TEST-AGENT-TWO", Row);

        var again = await Runner(kit).RunAsync(Run(kit, Fold(), [new FlowAnswerGiven("q1", "allow")]), Token);
        var record = kit.Harness.OrchestrationStore.ReadFoldRecord(kit.Layout.InstanceDirectory("fold", Agent))!;

        Assert.False(again.Passed);
        Assert.Equal([RowId, "D-TEST-AGENT-TWO"], again.DryRun!.NamedOf(ConsentKind.FoldNewRows));
        Assert.Empty(record.Granted);
        Assert.Equal(1, record.Asked!.AnswerAt);
        Assert.Null(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout));

        var applied = await Runner(kit).RunAsync(Run(kit, Fold(), [new FlowAnswerGiven("q1", "allow"), new FlowAnswerGiven("q2", "allow")]), Token);

        Assert.True(applied.Passed, applied.Error);
        Assert.Equal([RowId, "D-TEST-AGENT-TWO"], Texts(applied.Output!["rows"]));
    }

    /// <summary>
    /// What a step reads where it starts reaches the verb as its option: allow.new from an earlier step's output makes
    /// the row; a name that is no anchor's fails the step 12, naming the key.
    /// </summary>
    [Fact]
    public async Task AllowNewReadWhereTheStepStarts_ReachesTheFold_AndANameFilledWrongFails12()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync(Agent);
        kit.FileRow(Agent, RowId, Row);
        var step = Fold() with { Allow = new FlowAllow([], new FlowList([], "steps.find.output.ids"), [], null, []) };

        var wrong = await Runner(kit).RunAsync(Run(kit, step, read: Names("allow.new", "not an id")), Token);
        var made = await Runner(kit).RunAsync(Run(kit, step, read: Names("allow.new", RowId)), Token);

        Assert.Equal(HarnessExit.ConfigInvalid, wrong.Exit);
        Assert.StartsWith("'fold' takes allow: new 'not an id' ", wrong.Error, StringComparison.Ordinal);
        Assert.True(made.Passed, made.Error);
        Assert.Equal([RowId], Texts(made.Output!["rows"]));
    }

    /// <summary>
    /// A deletion folds what is left of its agent through the run's ledger of its folds as a fold does; discarding, it
    /// folds nothing, writes no intent, and answers that it wrote nothing.
    /// </summary>
    [Fact]
    public async Task ADeletion_FoldsThroughTheLedger_AndDiscardingFoldsNothing()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var discarding = await kit.CreateAgentAsync("gone");
        OrchestrationKit.Write(discarding, "a.txt", "one\nnot kept\n");

        var deleted = await Deleting(kit).RunAsync(Run(kit, Delete()), Token);
        var discarded = await Deleting(kit).RunAsync(Run(kit, Delete() with { DiscardUncommitted = true }, worksIn: "gone"), Token);

        Assert.True(deleted.Passed, deleted.Error);
        Assert.Equal(["b.txt"], Texts(deleted.Output!["written"]));
        Assert.Equal(AgentStates.Deleted, kit.Record(Agent).State);
        Assert.True(discarded.Passed, discarded.Error);
        Assert.Equal("""{"written":[],"deleted":[],"registries":[],"rows":[]}""", discarded.Output!.ToJsonString());

        var entry = Assert.Single(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Folds);

        Assert.Equal(("delete-agent", Agent, "applied"), (entry.Type, entry.Agent, entry.State));
        Assert.Equal("one\n", OrchestrationKit.Read(kit.Main, "a.txt"));
    }

    /// <summary>
    /// Where the run has a graph, a fold syncs the base by the paths it wrote and deleted alone - a change of the main tree
    /// it did not touch left to the next listing - and holds each view against it; a refresh syncs the view of the worktree
    /// it wrote; a deletion drops its agent's view; each sync a line of the run's sync log naming its step.
    /// </summary>
    [Fact]
    public async Task TheFoldFamily_SyncsTheRunsGraph_ByWhatEachWrote()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        File.Delete(Path.Combine(worktree, "docs", "x.md"));
        var request = new DssHarness.Core.Codebase.CodebaseGraphSync(new HarnessLayout(kit.Main, kit.Main), kit.Layout, DssHarness.Core.Codebase.CodebaseGraphShape.Default, OrchestrationKit.Settings.Root, Rebuild: false, "test");
        await kit.Harness.CodebaseGraph.SyncAsync(request with { Worktree = new DssHarness.Core.Codebase.CodebaseGraphWorktree(Agent, worktree) }, Token);
        var store = kit.Harness.OrchestrationStore;
        var untouched = store.ReadGraphManifest(kit.Layout)!.Files["a.txt"];
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nby hand\n");

        var folded = await Runner(kit).RunAsync(Run(kit, Fold()), Token);

        Assert.True(folded.Passed, folded.Error);
        var manifest = store.ReadGraphManifest(kit.Layout)!;
        var line = JsonNode.Parse(File.ReadLines(kit.Layout.GraphSyncFile).Last())!;

        Assert.Equal((await kit.Harness.RunGitAsync(kit.Main, ["hash-object", "b.txt"], Token)).StandardOutput.Trim(), manifest.Files["b.txt"].Blob);
        Assert.DoesNotContain("docs/x.md", manifest.Files.Keys);
        Assert.Equal(untouched, manifest.Files["a.txt"]);
        Assert.Equal(("base", "fold:fold", 2), ((string?)line["view"], (string?)line["why"], (int)line["listed"]!));
        Assert.Equal(manifest.Digest, store.ReadGraphView(kit.Layout, Agent)!.Base);
        Assert.Empty(store.ReadGraphView(kit.Layout, Agent)!.Files);
        Assert.True(store.ReadFolds(kit.Layout)!.Folds.Single().Graphed);

        var refresh = new WorkflowStep { Name = "refresh", Type = "refresh", Of = "made", Paths = new FlowList(["a.txt"], null) };
        var refreshed = await Refreshing(kit).RunAsync(Run(kit, refresh), Token);

        Assert.True(refreshed.Passed, refreshed.Error);
        Assert.Equal("one\nby hand\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal(["a.txt"], store.ReadGraphView(kit.Layout, Agent)!.Files.Keys);
        Assert.Equal((Agent, "refresh:refresh"), ((string?)JsonNode.Parse(File.ReadLines(kit.Layout.GraphSyncFile).Last())!["view"], (string?)JsonNode.Parse(File.ReadLines(kit.Layout.GraphSyncFile).Last())!["why"]));

        var deleted = await Deleting(kit).RunAsync(Run(kit, Delete()), Token);

        Assert.True(deleted.Passed, deleted.Error);
        Assert.Empty(store.GraphViews(kit.Layout));
        Assert.NotNull(store.ReadGraphManifest(kit.Layout));
    }

    /// <summary>
    /// What a fold changed that the graph did not take - its sync failed - stays owed in the run's ledger and is said; the
    /// next sync of the folds takes it, this step's next attempt's among them, and the ledger says the graph took it.
    /// </summary>
    [Fact]
    public async Task WhatAFoldChanged_ThatTheGraphDidNotTake_IsOwed_UntilASyncTakesIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var store = kit.Harness.OrchestrationStore;
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        await kit.Harness.CodebaseGraph.SyncAsync(GraphRequest(kit), Token);
        var graph = Substitute.For<DssHarness.Core.Codebase.ICodebaseGraphService>();
        graph.SyncPathsAsync(Arg.Any<DssHarness.Core.Codebase.CodebaseGraphSync>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DssHarness.Core.Codebase.CodebaseGraphSynced?>(new HarnessException(HarnessExit.ToolMissing, "git did not answer.")));
        var output = Substitute.For<DssHarness.Core.Output.IHarnessOutput>();
        var failing = new FlowGraphSyncs(graph, store, Ledger(kit), kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.FileSystem, output, TimeProvider.System);

        var folded = await new FoldStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, store, kit.Harness.AgentService, Ledger(kit), failing).RunAsync(Run(kit, Fold()), Token);

        var blob = (await kit.Harness.RunGitAsync(kit.Main, ["hash-object", "b.txt"], Token)).StandardOutput.Trim();
        Assert.True(folded.Passed, folded.Error);
        Assert.Null(store.ReadFolds(kit.Layout)!.Folds.Single().Graphed);
        Assert.NotEqual(blob, store.ReadGraphManifest(kit.Layout)!.Files["b.txt"].Blob);
        output.Received(1).Warn("fold", $"the codebase graph's view 'base' of run '{OrchestrationKit.Orchestrator}' was not synced (fold:fold): git did not answer. The step goes on; the paths it owes are synced again by the next fold or deletion, and by this step's next attempt.");

        // A sync by paths that takes nothing - the graph's manifest does not read - leaves what it was given owed, the
        // first fold's and the next's alike, and says why; an attempt that ends before it applies anything - its allow
        // filled wrong - takes every fold owed where it starts.
        var nothing = Substitute.For<DssHarness.Core.Codebase.ICodebaseGraphService>();
        nothing.SyncPathsAsync(Arg.Any<DssHarness.Core.Codebase.CodebaseGraphSync>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DssHarness.Core.Codebase.CodebaseGraphSynced?>(new HarnessException(HarnessExit.Refused, "'graph.json' does not read as one: its schema is 99.")));
        var taking = new FlowGraphSyncs(nothing, store, Ledger(kit), kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.FileSystem, output, TimeProvider.System);
        OrchestrationKit.Write(worktree, "c.txt", "three\n");
        var lines = File.ReadAllLines(kit.Layout.GraphSyncFile).Length;

        var next = await new FoldStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, store, kit.Harness.AgentService, Ledger(kit), taking).RunAsync(Run(kit, Fold(), attempt: 2), Token);

        Assert.True(next.Passed, next.Error);
        Assert.Equal([null, null], store.ReadFolds(kit.Layout)!.Folds.Select(fold => fold.Graphed));
        output.Received().Warn("fold", $"the codebase graph's view 'base' of run '{OrchestrationKit.Orchestrator}' was not synced (fold:fold): 'graph.json' does not read as one: its schema is 99. The step goes on; the paths the run's folds changed stay owed until a whole sync builds the graph again.");
        Assert.Equal("'graph.json' does not read as one: its schema is 99.", (string?)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllLines(kit.Layout.GraphSyncFile)[lines])!["failed"]);
        Assert.DoesNotContain("c.txt", store.ReadGraphManifest(kit.Layout)!.Files.Keys);

        var typo = Fold() with { Allow = new FlowAllow([], new FlowList([], "steps.find.output.ids"), [], null, []) };
        var taken = await Runner(kit).RunAsync(Run(kit, typo, attempt: 3, read: Names("allow.new", "not an id")), Token);

        Assert.Equal(HarnessExit.ConfigInvalid, taken.Exit);
        Assert.Equal([true, true], store.ReadFolds(kit.Layout)!.Folds.Select(fold => fold.Graphed));
        Assert.Equal(blob, store.ReadGraphManifest(kit.Layout)!.Files["b.txt"].Blob);
        Assert.Contains("c.txt", store.ReadGraphManifest(kit.Layout)!.Files.Keys);
    }

    /// <summary>
    /// The view a drop that failed left of a deleted agent is dropped by the engine's next sync of the run's graph, which
    /// drops the view of every agent no longer live.
    /// </summary>
    [Fact]
    public async Task AViewADropLeft_IsDroppedByTheEnginesNextSync()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var store = kit.Harness.OrchestrationStore;
        var worktree = await kit.CreateAgentAsync(Agent);
        await kit.Harness.CodebaseGraph.SyncAsync(GraphRequest(kit) with { Worktree = new DssHarness.Core.Codebase.CodebaseGraphWorktree(Agent, worktree) }, Token);
        var graph = Substitute.For<DssHarness.Core.Codebase.ICodebaseGraphService>();
        graph.DropViewAsync(Arg.Any<OrchestratorLayout>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("the view is held open.")));
        var output = Substitute.For<DssHarness.Core.Output.IHarnessOutput>();
        var failing = new FlowGraphSyncs(graph, store, Ledger(kit), kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.FileSystem, output, TimeProvider.System);

        var deleted = await new DeleteAgentStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, store, kit.Harness.AgentService, Ledger(kit), failing).RunAsync(Run(kit, Delete()), Token);

        Assert.True(deleted.Passed, deleted.Error);
        Assert.Equal([Agent], store.GraphViews(kit.Layout));
        output.Received().Warn("delete-agent", $"the codebase graph's view '{Agent}' of run '{OrchestrationKit.Orchestrator}' was not dropped (delete:delete-agent): the view is held open. The step goes on; the engine's next sync of the run's graph drops it again.");

        await kit.Harness.GraphSyncs.AtStartAsync(Run(kit, new WorkflowStep { Name = "think", Type = "reason" }, worksIn: null), Token);

        Assert.Empty(store.GraphViews(kit.Layout));
    }

    /// <summary>
    /// A rebase that moved an agent's worktree syncs that agent's view, and a refresh of an agent the graph has no view of
    /// makes none: the graph keeps the views the run made.
    /// </summary>
    [Fact]
    public async Task ARebaseSyncsItsTargetsView_AndARefreshMakesNoneTheRunDidNot()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var store = kit.Harness.OrchestrationStore;
        var worktree = await kit.CreateAgentAsync(Agent);
        await kit.Harness.CodebaseGraph.SyncAsync(GraphRequest(kit), Token);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nby hand\n");

        var refreshed = await Refreshing(kit).RunAsync(Run(kit, new WorkflowStep { Name = "refresh", Type = "refresh", Of = "made", Paths = new FlowList(["a.txt"], null) }), Token);

        Assert.True(refreshed.Passed, refreshed.Error);
        Assert.Empty(store.GraphViews(kit.Layout));

        await kit.Harness.CodebaseGraph.SyncAsync(GraphRequest(kit) with { Worktree = new DssHarness.Core.Codebase.CodebaseGraphWorktree(Agent, worktree) }, Token);
        OrchestrationKit.Write(kit.Main, "c.txt", "three\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a file the agent's base lacks", Token);

        var rebased = await new RebaseStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, store, kit.Harness.AgentService, kit.Harness.GraphSyncs)
            .RunAsync(Run(kit, new WorkflowStep { Name = "rebase", Type = "rebase", Of = "made" }), Token);
        var line = JsonNode.Parse(File.ReadLines(kit.Layout.GraphSyncFile).Last())!;

        Assert.True(rebased.Passed, rebased.Error);
        Assert.Equal((Agent, "rebase:rebase"), ((string?)line["view"], (string?)line["why"]));
        Assert.True(File.Exists(Path.Combine(worktree, "c.txt")));
    }

    /// <summary>A whole sync of the base of the kit's run.</summary>
    private static DssHarness.Core.Codebase.CodebaseGraphSync GraphRequest(OrchestrationKit kit)
        => new(new HarnessLayout(kit.Main, kit.Main), kit.Layout, DssHarness.Core.Codebase.CodebaseGraphShape.Default, OrchestrationKit.Settings.Root, Rebuild: false, "test");

    /// <summary>
    /// A refresh refused for conflicting merges alone hands every path that does not conflict, then asks foldConflicts of
    /// those it left, naming them - kept in its record; one over a clean merge nobody asked for asks foldTwoChanges, and
    /// answered merge takes it.
    /// </summary>
    [Fact]
    public async Task ARefresh_HandsWhatDoesNotConflict_ThenAsksOfTheRest_AndAMergeAnswerTakesACleanOne()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text());
        await kit.Harness.CommitAllAsync(kit.Main, "a file both trees will change", Token);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, Both, AgentFoldMergeTests.Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((2, "MAIN 2")));
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var step = new WorkflowStep { Name = "refresh", Type = "refresh", Of = "made", Paths = new FlowList(["a.txt", Both], null) };

        var refreshed = await Refreshing(kit).RunAsync(Run(kit, step), Token);

        Assert.Equal("foldConflicts", FlowQuestions.FoldQuestionOf(refreshed.DryRun!)!.Kind);
        Assert.Equal([Both], refreshed.DryRun!.Conflicting);
        Assert.Equal("one\nmain edit\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Equal([Both], kit.Harness.OrchestrationStore.ReadFoldRecord(kit.Layout.InstanceDirectory("refresh", Agent))!.LeftConflicting);

        // The person reconciles it by hand to what merges clean, and the next attempt asks the merge of it.
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((9, "MAIN 9")));

        var merging = await Refreshing(kit).RunAsync(Run(kit, step, attempt: 2), Token);

        Assert.Equal("foldTwoChanges", FlowQuestions.FoldQuestionOf(merging.DryRun!)!.Kind);
        Assert.Equal([Both], merging.DryRun!.NamedOf(ConsentKind.FoldTwoChanges));

        var merged = await Refreshing(kit).RunAsync(Run(kit, step, [new FlowAnswerGiven("q2", "merge")], attempt: 2), Token);

        Assert.True(merged.Passed, merged.Error);
        Assert.Contains("MAIN 9", OrchestrationKit.Read(worktree, Both), StringComparison.Ordinal);
        Assert.Contains("AGENT 2", OrchestrationKit.Read(worktree, Both), StringComparison.Ordinal);
    }

    /// <summary>A rebase over a conflict moves nothing and asks foldConflicts, its base where it was.</summary>
    [Fact]
    public async Task ARebaseOverAConflict_MovesNothing_AndAsksFoldConflicts()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        var based = kit.Record(Agent).Base;
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);
        var step = new WorkflowStep { Name = "rebase", Type = "rebase", Of = "made" };

        var asked = await new RebaseStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, kit.Harness.AgentService, kit.Harness.GraphSyncs).RunAsync(Run(kit, step), Token);

        Assert.Equal("foldConflicts", FlowQuestions.FoldQuestionOf(asked.DryRun!)!.Kind);
        Assert.Equal(["b.txt"], asked.DryRun!.Conflicting);
        Assert.Equal(based, kit.Record(Agent).Base);
    }

    /// <summary>
    /// A step of others keeps what it did to each target in its own directory under targets/&lt;agent&gt;/, and a refresh
    /// whose paths expression yielded none is done, having weighed nothing.
    /// </summary>
    [Fact]
    public async Task AStepOfOthers_KeepsEachTargetsRecordUnderTargets_AndARefreshOfNothingIsDone()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var others = new WorkflowStep { Name = "refresh", Type = "refresh", Others = "made", Paths = new FlowList(["a.txt"], null) };

        var refreshed = await Refreshing(kit).RunAsync(Run(kit, others, worksIn: null, target: Agent), Token);
        var none = Substitute.For<IAgentService>();
        var nothing = await new RefreshStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, none, kit.Harness.GraphSyncs).RunAsync(
            Run(kit, others with { Paths = new FlowList([], "steps.find.output.paths") }, worksIn: null, target: Agent, read: Names("paths")),
            Token);

        Assert.True(refreshed.Passed, refreshed.Error);
        Assert.Equal("one\nmain edit\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.NotNull(kit.Harness.OrchestrationStore.ReadFoldRecord(OrchestratorLayout.TargetDirectory(kit.Layout.InstanceDirectory("refresh", null), Agent)));
        Assert.Null(kit.Harness.OrchestrationStore.ReadFoldRecord(kit.Layout.InstanceDirectory("refresh", Agent)));
        Assert.True(nothing.Passed);
        _ = await none.DidNotReceive().RefreshAsync(Arg.Any<RefreshAgentRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The step reads its dry run's standing, typed, never its code: a dry run that could not be weighed ends it with its
    /// own code; refused or lacking a consent, it asks - whatever the code; sound with a run holding its tree, it ends 13
    /// with the cause of a held lock where that run is the one thing in the way; holding only a copy, 21; sound and free,
    /// it applies.
    /// </summary>
    [Theory]
    [InlineData("unweighed", HarnessExit.CommandFailed, "ends", HarnessExit.CommandFailed, OutcomeCause.None)]
    [InlineData("conflict", HarnessExit.Refused, "asks", HarnessExit.Refused, OutcomeCause.None)]
    [InlineData("other", HarnessExit.Refused, "asks", HarnessExit.Refused, OutcomeCause.None)]
    [InlineData("consent", HarnessExit.Refused, "asks", HarnessExit.Refused, OutcomeCause.None)]
    [InlineData("heldTree", HarnessExit.Refused, "ends", HarnessExit.Refused, OutcomeCause.LockHeld)]
    [InlineData("heldCopy", HarnessExit.Incomplete, "ends", HarnessExit.Incomplete, OutcomeCause.None)]
    [InlineData("unreadLock", HarnessExit.Refused, "ends", HarnessExit.Refused, OutcomeCause.None)]
    [InlineData("sound", HarnessExit.Success, "applies", HarnessExit.Success, OutcomeCause.None)]
    public async Task TheDryRunsStanding_DecidesTheStep_NeverItsCode(string row, int dryCode, string does, int stepCode, OutcomeCause cause)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var (standing, refusals, needs, dryCause) = row switch
        {
            "unweighed" => ((RequestStanding?)null, Array.Empty<RefusalKind>(), Array.Empty<ConsentKind>(), OutcomeCause.None),
            "conflict" => (RequestStanding.Refused, [RefusalKind.Conflict], [], OutcomeCause.None),
            "other" => (RequestStanding.Refused, [RefusalKind.Other], [], OutcomeCause.None),
            "consent" => (RequestStanding.NeedsConsent, [], [ConsentKind.FoldNewRows], OutcomeCause.None),
            "heldTree" => (RequestStanding.Sound, [], [], OutcomeCause.LockHeld),
            _ => (RequestStanding.Sound, Array.Empty<RefusalKind>(), Array.Empty<ConsentKind>(), OutcomeCause.None),
        };
        var dry = Answer(dryCode, "the dry run", standing, needs, refusals, dryCause);
        var agents = Scripted(dry, Answer(HarnessExit.Success, "folded", RequestStanding.Sound, [], []));

        var result = await new FoldStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, agents, Ledger(kit), kit.Harness.GraphSyncs).RunAsync(Run(kit, Fold()), Token);

        switch (does)
        {
            case "asks":
                Assert.NotNull(result.DryRun);
                Assert.Equal(stepCode, result.Exit);
                Assert.Equal(row switch { "conflict" => "foldConflicts", "consent" => "foldNewRows", _ => null }, FlowQuestions.FoldQuestionOf(result.DryRun)?.Kind);
                break;
            case "ends":
                Assert.Null(result.DryRun);
                Assert.False(result.Passed);
                Assert.Equal((stepCode, cause), (result.Exit, result.Cause));
                break;
            default:
                Assert.True(result.Passed, result.Error);
                break;
        }

        _ = await agents.Received(does == "applies" ? 1 : 0).FoldAsync(Arg.Is<FoldAgentRequest>(request => request.Apply), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An apply refused under its hold for what its dry run never named - its trees moved between the two - is weighed
    /// again, at most three times, then fails with the apply's own words; one a run holds the tree of is waited out by the
    /// retry table, never weighed again here; and the apply is held to exactly what the dry run named.
    /// </summary>
    [Fact]
    public async Task AnApplyItsTreesMovedUnder_IsWeighedAgain_AtMostThreeTimes()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var named = new AgentPlanned([], [], [], [], null) { Written = ["b.txt"] };
        var sound = Answer(HarnessExit.Success, "would fold", RequestStanding.Sound, [], [], planned: named);
        var moved = Answer(HarnessExit.Refused, "the trees moved", RequestStanding.Refused, [], [RefusalKind.Other]);
        var held = Answer(HarnessExit.Refused, "held", RequestStanding.Sound, [], [], OutcomeCause.LockHeld);

        var once = Scripted(sound, moved, Answer(HarnessExit.Success, "folded", RequestStanding.Sound, [], []));
        var always = Scripted(sound, moved);
        var waiting = Scripted(sound, held);

        var passed = await new FoldStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, once, Ledger(kit), kit.Harness.GraphSyncs).RunAsync(Run(kit, Fold()), Token);
        var failed = await new FoldStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, always, Ledger(kit), kit.Harness.GraphSyncs).RunAsync(Run(kit, Fold()), Token);
        var lockHeld = await new FoldStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, waiting, Ledger(kit), kit.Harness.GraphSyncs).RunAsync(Run(kit, Fold()), Token);

        Assert.True(passed.Passed, passed.Error);
        _ = await once.Received(2).FoldAsync(Arg.Is<FoldAgentRequest>(request => request.Apply && ReferenceEquals(request.Expected, named)), Arg.Any<CancellationToken>());
        Assert.Equal((HarnessExit.Refused, "the trees moved"), (failed.Exit, failed.Error));
        _ = await always.Received(FoldFamilyStepRunner.MostPasses).FoldAsync(Arg.Is<FoldAgentRequest>(request => request.Apply), Arg.Any<CancellationToken>());
        Assert.Equal((HarnessExit.Refused, OutcomeCause.LockHeld), (lockHeld.Exit, lockHeld.Cause));
        _ = await waiting.Received(1).FoldAsync(Arg.Is<FoldAgentRequest>(request => request.Apply), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The ledger settles each fold as it ended: an apply that stopped (21) stopped, with what it recorded it left - each
    /// path as it found it where it never began to write; one that threw after its intent was written interrupted, before
    /// the exception goes on; and an intent a crash left - nothing written after it - interrupted where the next attempt
    /// starts, before it folds anything; an apply that went through, by what it recorded under its hold.
    /// </summary>
    [Fact]
    public async Task TheLedger_SettlesAStoppedApply_AThrownOne_AndAnIntentACrashLeft()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var named = new AgentPlanned([], [], [], [], null) { Written = ["b.txt"] };
        var sound = Answer(HarnessExit.Success, "would fold", RequestStanding.Sound, [], [], planned: named);
        var throwing = Substitute.For<IAgentService>();
        throwing.FoldAsync(Arg.Any<FoldAgentRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<FoldAgentRequest>().Apply ? Task.FromException<UseCaseResult<AgentVerbFacts>>(new IOException("the disk went away")) : Task.FromResult(sound));
        FoldStepRunner Folding(IAgentService agents) => new(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, agents, Ledger(kit), kit.Harness.GraphSyncs);

        var stopped = await Folding(Scripted(sound, Answer(HarnessExit.Incomplete, "stopped part way", null, [], []))).RunAsync(Run(kit, Fold()), Token);
        var thrown = await Assert.ThrowsAsync<IOException>(() => Folding(throwing).RunAsync(Run(kit, Fold(), attempt: 2), Token));

        // Settled before the exception went on, not left for the next attempt.
        Assert.Equal(FlowFoldEntry.Interrupted, kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Of("f2")!.State);

        var crashed = await Ledger(kit).IntendAsync(kit.Layout, kit.Main, "fold", 3, "fold", Agent, named, Token);
        var after = await Folding(Scripted(sound, Answer(HarnessExit.Success, "folded", RequestStanding.Sound, [], []))).RunAsync(Run(kit, Fold(), attempt: 4), Token);
        var folds = kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Folds;

        Assert.Equal((HarnessExit.Incomplete, "stopped part way"), (stopped.Exit, stopped.Error));
        Assert.Equal("the disk went away", thrown.Message);
        Assert.Equal("f3", crashed);
        Assert.True(after.Passed, after.Error);
        Assert.Equal(
            [("f1", 1, "stopped"), ("f2", 2, "interrupted"), ("f3", 3, "interrupted"), ("f4", 4, "applied")],
            folds.Select(fold => (fold.Id, fold.Attempt, fold.State)));
        Assert.All(folds, fold => Assert.False(Assert.Single(fold.Paths).Changed));
        Assert.Empty(FoldLedger.Uncommitted(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout), differing: null));
    }

    /// <summary>
    /// An answer that came back to a dry run that could not be weighed waits on, where it stands: the step ends with the
    /// dry run's own code, its question kept, and the attempt that can weigh it gives the consent by name.
    /// </summary>
    [Fact]
    public async Task AnAnswerToADryRunNotWeighed_WaitsOn_ForTheAttemptThatCanWeighIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var lacking = Answer(HarnessExit.Refused, "lacks --new", RequestStanding.NeedsConsent, [ConsentKind.FoldNewRows], [], planned: new AgentPlanned([], [], [], [], null) { NewRows = [RowId] });
        var unweighed = Answer(HarnessExit.CommandFailed, "could not be read", null, [], []);
        var drys = new Queue<UseCaseResult<AgentVerbFacts>>([lacking, unweighed, lacking, lacking]);
        var agents = Substitute.For<IAgentService>();
        agents.FoldAsync(Arg.Any<FoldAgentRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<FoldAgentRequest>().Apply ? Answer(HarnessExit.Success, "folded", RequestStanding.Sound, [], [])
                : call.Arg<FoldAgentRequest>().Allowances.New.Count > 0 ? Answer(HarnessExit.Success, "would fold", RequestStanding.Sound, [], []) : drys.Dequeue()));
        var runner = new FoldStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, agents, Ledger(kit), kit.Harness.GraphSyncs);
        var answered = new[] { new FlowAnswerGiven("q1", "allow") };

        var asked = await runner.RunAsync(Run(kit, Fold()), Token);
        var unread = await runner.RunAsync(Run(kit, Fold(), answered), Token);
        var record = kit.Harness.OrchestrationStore.ReadFoldRecord(kit.Layout.InstanceDirectory("fold", Agent))!;
        var applied = await runner.RunAsync(Run(kit, Fold(), answered), Token);

        Assert.NotNull(asked.DryRun);
        Assert.Equal((HarnessExit.CommandFailed, "could not be read"), (unread.Exit, unread.Error));
        Assert.Equal(new FlowFoldAsked("foldNewRows", [RowId], 0) with { Named = record.Asked!.Named }, record.Asked);
        Assert.True(applied.Passed, applied.Error);
        Assert.Equal([RowId], Assert.Single(kit.Harness.OrchestrationStore.ReadFoldRecord(kit.Layout.InstanceDirectory("fold", Agent))!.Granted).Named);
    }

    /// <summary>
    /// A deletion of an agent whose deletion began - closed, its fold made - folds nothing: it writes no intent, holds no
    /// path to within, and takes no allowance, whatever its allow names; a fold a crash left after the closing - what it
    /// left recorded under its hold, and nothing after - is settled all the same, by that record, and the run's graph's
    /// base is synced by what it changed.
    /// </summary>
    [Fact]
    public async Task ADeletionOfAClosedAgent_FoldsNothing_AndTakesNoAllowance()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync(Agent);
        kit.Harness.OrchestrationStore.UpdateAgent(kit.Layout, Agent, record => record with
        {
            State = AgentStates.Closed,
            Abandoned = false,
            Closing = new AgentClosing { At = DateTimeOffset.UnixEpoch, Evidence = string.Empty, Stamp = "stamp", Held = [] },
        });
        var sound = Answer(HarnessExit.Success, "would delete", RequestStanding.Sound, [], [], planned: new AgentPlanned([], [], [], [], null) { Written = ["c.txt"] });
        var agents = Substitute.For<IAgentService>();
        agents.DeleteAsync(Arg.Any<DeleteAgentRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(sound));
        var step = Delete() with { Within = new FlowList(["b.txt"], null), Allow = new FlowAllow([], new FlowList(["not an id"], null), [], null, []) };
        var crashed = await Ledger(kit).IntendAsync(kit.Layout, kit.Main, "delete", 1, "delete-agent", Agent, new AgentPlanned([], [], [], [], null) { Written = ["b.txt"] }, Token);

        // The run has a graph, and what the crashed fold wrote reaches its base where the ledger settles it.
        await kit.Harness.CodebaseGraph.SyncAsync(new DssHarness.Core.Codebase.CodebaseGraphSync(new HarnessLayout(kit.Main, kit.Main), kit.Layout, DssHarness.Core.Codebase.CodebaseGraphShape.Default, OrchestrationKit.Settings.Root, Rebuild: false, "test"), Token);
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nwritten by the fold that crashed\n");
        await OrchestrationKit.WrittenAsync(Ledger(kit), kit.Layout, kit.Main, crashed);

        var deleted = await new DeleteAgentStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, agents, Ledger(kit), kit.Harness.GraphSyncs).RunAsync(Run(kit, step, attempt: 2), Token);

        Assert.True(deleted.Passed, deleted.Error);
        Assert.Equal(
            (await kit.Harness.RunGitAsync(kit.Main, ["hash-object", "b.txt"], Token)).StandardOutput.Trim(),
            kit.Harness.OrchestrationStore.ReadGraphManifest(kit.Layout)!.Files["b.txt"].Blob);
        Assert.Contains("\"why\":\"delete:delete-agent\"", File.ReadLines(kit.Layout.GraphSyncFile).Last(), StringComparison.Ordinal);
        Assert.Equal((crashed, FlowFoldEntry.Interrupted), Assert.Single(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Folds) is var entry ? (entry.Id, entry.State) : default);
        _ = await agents.Received(1).DeleteAsync(
            Arg.Is<DeleteAgentRequest>(request => request.Apply && request.Expected == null && request.Allowances.New.Count == 0),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A refresh whose merge names a path whose merge conflicts hands every other path all the same - the conflicting one
    /// left, never merged - and asks foldConflicts of it.
    /// </summary>
    [Fact]
    public async Task ARefreshWhoseMergeNamesAConflictingPath_HandsTheRest_AndAsksOfIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text());
        await kit.Harness.CommitAllAsync(kit.Main, "a file both trees will change", Token);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, Both, AgentFoldMergeTests.Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((2, "MAIN 2")));
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var step = new WorkflowStep { Name = "refresh", Type = "refresh", Of = "made", Paths = new FlowList(["a.txt", Both], null), Merge = new FlowList([Both], null) };

        var refreshed = await Refreshing(kit).RunAsync(Run(kit, step), Token);

        Assert.Equal("foldConflicts", FlowQuestions.FoldQuestionOf(refreshed.DryRun!)!.Kind);
        Assert.Equal([Both], refreshed.DryRun!.Conflicting);
        Assert.Equal("one\nmain edit\n", OrchestrationKit.Read(worktree, "a.txt"));
        Assert.Contains("AGENT 2", OrchestrationKit.Read(worktree, Both), StringComparison.Ordinal);
    }

    /// <summary>
    /// A ledger that cannot settle a fold loses nothing the apply said: an apply that threw part way through its write, and
    /// could not record what it left, goes on with its own exception - the ledger's read of the tree failing as well, the
    /// fold left for the next attempt, which settles it - and one that answered is kept in the step's directory, with the
    /// ledger's entry it was intended under, before a settle that fails: here, of an apply that went through and recorded
    /// no write, which the engine never answers.
    /// </summary>
    [Fact]
    public async Task ALedgerThatCannotSettle_KeepsTheApplysOwnException_AndWhatItAnswered()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "b.txt", "two\n");
        var named = new AgentPlanned([], [], [], [], null) { Written = ["b.txt"] };
        var sound = Answer(HarnessExit.Success, "would fold", RequestStanding.Sound, [], [], planned: named);
        var folded = Answer(HarnessExit.Success, "folded", RequestStanding.Sound, [], []);
        var tree = new UnreadableWhenAsked(kit.Harness.FileSystem, Path.Combine(kit.Main, "b.txt"));
        var ledger = new FoldLedger(kit.Harness.OrchestrationStore, tree, TimeProvider.System);
        var agents = Substitute.For<IAgentService>();
        var applies = 0;
        agents.FoldAsync(Arg.Any<FoldAgentRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.Arg<FoldAgentRequest>();

                if (!request.Apply)
                {
                    return sound;
                }

                // The tree cannot be read from the moment the apply is called: the read after it fails. The first apply
                // begins its write, as the verb records it under its hold, and throws before it can record what it left.
                tree.Unreadable = true;

                if (++applies > 1)
                {
                    return folded;
                }

                await OrchestrationKit.WritingAsync(request.Intent!.Ledger, request.Intent.Layout, request.StartDirectory, request.Intent.Id);
                throw new IOException("the disk went away");
            });
        FoldStepRunner Folding() => new(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, agents, ledger, kit.Harness.GraphSyncs);

        var thrown = await Assert.ThrowsAsync<IOException>(() => Folding().RunAsync(Run(kit, Fold()), Token));

        Assert.Equal("the disk went away", thrown.Message);
        Assert.Equal(FlowFoldEntry.Writing, kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Of("f1")!.State);

        tree.Unreadable = false;
        var unsettled = await Assert.ThrowsAsync<HarnessException>(() => Folding().RunAsync(Run(kit, Fold(), attempt: 2), Token));
        var applied = kit.Harness.OrchestrationStore.ReadFoldRecord(kit.Layout.InstanceDirectory("fold", Agent))!.Applied;

        Assert.Equal(HarnessExit.InternalError, unsettled.ExitCode);
        Assert.Equal((HarnessExit.Success, "folded", "f2"), (applied!.ExitCode, applied.Summary, applied.Ledger));
        Assert.Equal(
            [("f1", FlowFoldEntry.Interrupted), ("f2", FlowFoldEntry.Intended)],
            kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Folds.Select(fold => (fold.Id, fold.State)));
    }

    /// <summary>
    /// A deletion run again once its agent was deleted - its ending cut off before it was recorded, by a stop that drained
    /// it, a time limit or a crash - is done, with the output its directory kept of the apply: it calls nothing, folds
    /// nothing again, and its agent counts open no more.
    /// </summary>
    [Fact]
    public async Task ADeletionRunAgainOnceItsAgentWasDeleted_IsDone_WithWhatItsApplyKept()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");

        var deleted = await Deleting(kit).RunAsync(Run(kit, Delete()), Token);
        var agents = Substitute.For<IAgentService>();
        var again = await new DeleteAgentStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, agents, Ledger(kit), kit.Harness.GraphSyncs)
            .RunAsync(Run(kit, Delete(), attempt: 2), Token);

        Assert.True(deleted.Passed, deleted.Error);
        Assert.True(again.Passed, again.Error);
        Assert.Equal(deleted.Output!.ToJsonString(), again.Output!.ToJsonString());
        Assert.Single(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Folds);
        Assert.Empty(agents.ReceivedCalls());

        // With no apply kept - its record never written - it is done all the same, saying nothing of what it folded.
        File.Delete(Path.Combine(kit.Layout.InstanceDirectory("delete", Agent), OrchestratorLayout.StepFoldFileName));
        var bare = await Deleting(kit).RunAsync(Run(kit, Delete(), attempt: 3), Token);

        Assert.True(bare.Passed, bare.Error);
        Assert.Null(bare.Output);
    }

    /// <summary>
    /// A fold's within is matched by the case rule it is given, the one paths are compared by where the tree is: a path
    /// written in another case is within it where names ignore case, and outside it where they do not; with no within,
    /// nothing is outside.
    /// </summary>
    [Fact]
    public void AFoldsWithin_MatchesByTheCaseRuleOfTheTree()
    {
        Assert.Empty(FoldWithin.Outside(["Src/A.cs", "src/b.cs"], ["src/**"], StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Src/A.cs"], FoldWithin.Outside(["Src/A.cs", "src/b.cs"], ["src/**"], StringComparison.Ordinal));
        Assert.Empty(FoldWithin.Outside(["anything.txt"], null, StringComparison.Ordinal));
    }

    /// <summary>
    /// A fold takes the main tree's lock, as every engine write to the main tree does, waiting out a side-task's write or
    /// command that holds it: nothing reaches the tree while the other side's hold stands, and once it lets go the fold
    /// goes on and its step's output says how long it waited and for what, beside what it folded.
    /// </summary>
    [Fact]
    public async Task AFold_WaitsOutASideTasksHoldOfTheMainTree_AndItsOutputSaysWhatItWaitedFor()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var tree = MainTreeWriteLock.NameFor(kit.Main);

        using var sideTask = MachineWideMutex.TryHold(tree, "a test as a side-task");

        Assert.NotNull(sideTask);

        var waits = MachineWideHold.WaitBegins(tree);
        var folding = Task.Run(() => Runner(kit).RunAsync(Run(kit, Fold()), Token));

        // Let go only once the fold waits for the lock: it waited then, and nothing of it reached the tree meanwhile.
        await waits.WaitAsync(Token);
        Assert.False(folding.IsCompleted, "the fold completed while a side-task held the main tree's lock");
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));

        sideTask.Dispose();

        var folded = await folding;

        Assert.True(folded.Passed, folded.Error);
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Equal(MainTreeWriteLock.WaitedOutOn, WaitedOf(folded)["for"]!.GetValue<string>());

        // Beside the wait, the fold's own output, as a fold that waited for nothing answers it.
        var output = folded.Output!.AsObject();
        output.Remove(MainTreeWriteLock.WaitedMember);
        Assert.Equal("""{"written":["b.txt"],"deleted":[],"registries":[],"rows":[]}""", output.ToJsonString());
    }

    /// <summary>
    /// A fold weighs the main tree under the lock it writes it under: a side-task's write made while the fold waited for
    /// the lock is weighed - here a change of the very file the fold would write, which the agent changed too - and never
    /// met part way through the fold's writes, so the fold asks of it rather than stopping with half of itself written, and
    /// the side-task's write stands.
    /// </summary>
    [Fact]
    public async Task AFold_WeighsUnderTheTreesLock_WhatASideTaskWroteWhileItWaited()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var tree = MainTreeWriteLock.NameFor(kit.Main);
        using var sideTask = MachineWideMutex.TryHold(tree, "a test as a side-task");

        Assert.NotNull(sideTask);

        var waits = MachineWideHold.WaitBegins(tree);
        var folding = Task.Run(() => Runner(kit).RunAsync(Run(kit, Fold()), Token));

        await waits.WaitAsync(Token);

        // The side-task's write, made under its hold while the fold's apply waits for the lock.
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nthe side-task's edit\n");
        sideTask.Dispose();

        var folded = await folding;

        Assert.False(folded.Passed);
        Assert.Equal("foldConflicts", FlowQuestions.FoldQuestionOf(folded.DryRun!)!.Kind);
        Assert.Equal(["b.txt"], folded.DryRun!.Conflicting);
        Assert.Equal("two\nthe side-task's edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// A refresh's apply takes the main tree's lock and weighs the main tree again under it: nothing is handed while a
    /// side-task's hold stands, and what the side-task wrote meanwhile is weighed with the rest and handed - never left out
    /// for having moved after the dry run weighed the tree - its output saying what it waited out.
    /// </summary>
    [Fact]
    public async Task ARefresh_WaitsOutASideTasksHold_AndHandsWhatTheMainTreeHoldsOnceItLetsGo()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nby hand\n");
        var tree = MainTreeWriteLock.NameFor(kit.Main);
        using var sideTask = MachineWideMutex.TryHold(tree, "a test as a side-task");

        Assert.NotNull(sideTask);

        var waits = MachineWideHold.WaitBegins(tree);
        var refreshing = Task.Run(() => Refreshing(kit).RunAsync(Run(kit, Refresh("a.txt", "b.txt")), Token));

        await waits.WaitAsync(Token);
        Assert.False(refreshing.IsCompleted, "the refresh completed while a side-task held the main tree's lock");
        Assert.Equal("one\n", OrchestrationKit.Read(worktree, "a.txt"));

        // The side-task's write, made under its hold while the refresh's apply waits for the lock.
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nby the side-task\n");
        sideTask.Dispose();

        var refreshed = await refreshing;

        Assert.True(refreshed.Passed, refreshed.Error);
        Assert.Equal(("one\nby hand\n", "two\nby the side-task\n"), (OrchestrationKit.Read(worktree, "a.txt"), OrchestrationKit.Read(worktree, "b.txt")));
        Assert.Equal([MainTreeWriteLock.WaitedMember], refreshed.Output!.AsObject().Select(member => member.Key));
        Assert.Equal(MainTreeWriteLock.WaitedOutOn, WaitedOf(refreshed)["for"]!.GetValue<string>());
    }

    /// <summary>
    /// A rebase's apply takes the main tree's lock - what it keeps alive in the main checkout and settles there are the main
    /// tree's git's own: nothing of the move is made while a side-task's hold stands, and once it lets go the base moves,
    /// the step's output saying what it waited out.
    /// </summary>
    [Fact]
    public async Task ARebase_WaitsOutASideTasksHold_AndMovesTheBaseOnceItLetsGo()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        var based = kit.Record(Agent).Base;
        OrchestrationKit.Write(kit.Main, "c.txt", "three\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a file the agent's base lacks", Token);
        var head = await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token);
        var tree = MainTreeWriteLock.NameFor(kit.Main);

        using var sideTask = MachineWideMutex.TryHold(tree, "a test as a side-task");

        Assert.NotNull(sideTask);

        var waits = MachineWideHold.WaitBegins(tree);
        var rebasing = Task.Run(() => Rebasing(kit).RunAsync(Run(kit, Rebase()), Token));

        await waits.WaitAsync(Token);
        Assert.False(rebasing.IsCompleted, "the rebase completed while a side-task held the main tree's lock");
        Assert.Equal((based, null), (kit.Record(Agent).Base, kit.Record(Agent).Moving));
        Assert.False(File.Exists(Path.Combine(worktree, "c.txt")));

        sideTask.Dispose();

        var rebased = await rebasing;

        Assert.True(rebased.Passed, rebased.Error);
        Assert.Equal(head, kit.Record(Agent).Base);
        Assert.Equal("three\n", OrchestrationKit.Read(worktree, "c.txt"));
        Assert.Equal(MainTreeWriteLock.WaitedOutOn, WaitedOf(rebased)["for"]!.GetValue<string>());
    }

    /// <summary>
    /// A fold family step waiting for the main tree's lock - a fold, a deletion that folds, a refresh, a rebase - is ended
    /// by its own stop, as a run's pause or stop ends it: the wait is let go, nothing of the apply is weighed or written,
    /// and the agent stands as it stood.
    /// </summary>
    [Theory]
    [InlineData("fold")]
    [InlineData("delete-agent")]
    [InlineData("refresh")]
    [InlineData("rebase")]
    public async Task AFoldFamilyStepWaitingForTheTreesLock_IsEndedByItsStop_NothingWritten(string type)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        var based = kit.Record(Agent).Base;
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nby hand\n");

        if (type == "rebase")
        {
            OrchestrationKit.Write(kit.Main, "c.txt", "three\n");
            await kit.Harness.CommitAllAsync(kit.Main, "a file the agent's base lacks", Token);
        }

        var (runner, step) = type switch
        {
            "fold" => ((IFlowStepTypeRunner)Runner(kit), Fold()),
            "delete-agent" => (Deleting(kit), Delete()),
            "refresh" => (Refreshing(kit), Refresh("a.txt")),
            _ => (Rebasing(kit), Rebase()),
        };
        var tree = MainTreeWriteLock.NameFor(kit.Main);

        using var sideTask = MachineWideMutex.TryHold(tree, "a test as a side-task");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Assert.NotNull(sideTask);

        var waits = MachineWideHold.WaitBegins(tree);
        var running = Task.Run(() => runner.RunAsync(Run(kit, step), stop.Token));

        await waits.WaitAsync(Token);
        await stop.CancelAsync();

        // Bounded, so a wait the stop does not end fails here rather than holding the suite.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromMinutes(2), Token));

        Assert.Equal(("one\nby hand\n", "two\n"), (OrchestrationKit.Read(kit.Main, "a.txt"), OrchestrationKit.Read(kit.Main, "b.txt")));
        Assert.Equal(("one\n", "two\nagent edit\n"), (OrchestrationKit.Read(worktree, "a.txt"), OrchestrationKit.Read(worktree, "b.txt")));
        Assert.Equal((AgentStates.Live, based, null), (kit.Record(Agent).State, kit.Record(Agent).Base, kit.Record(Agent).Moving));
        Assert.False(File.Exists(Path.Combine(worktree, "c.txt")));
    }

    /// <summary>
    /// A fold, and a deletion that folds, stopped while it waits for the main tree's lock - a side-task's command holding
    /// it, rewriting the very path the fold plans to write - wrote nothing: its ledger entry is settled stopped with each
    /// path as it found it, the side-task's bytes never read as the fold's, and the commit after it owes nothing, so it
    /// commits nothing.
    /// </summary>
    [Theory]
    [InlineData("fold")]
    [InlineData("delete-agent")]
    public async Task AFoldStoppedWhileASideTaskWritesWhatItPlans_RecordsNothingChanged_AndItsCommitTakesNothing(string type)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var (runner, step) = type == "fold" ? ((IFlowStepTypeRunner)Runner(kit), Fold()) : (Deleting(kit), Delete());
        var tree = MainTreeWriteLock.NameFor(kit.Main);
        var head = await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token);

        using (var sideTask = MachineWideMutex.TryHold(tree, "a test as a side-task"))
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);

            Assert.NotNull(sideTask);

            var waits = MachineWideHold.WaitBegins(tree);
            var running = Task.Run(() => runner.RunAsync(Run(kit, step), stop.Token));

            await waits.WaitAsync(Token);

            // The side-task's command rewrites what the fold plans to write, and the run is paused while it does.
            OrchestrationKit.Write(kit.Main, "b.txt", "two\nformatted\n");
            await stop.CancelAsync();

            // Bounded, so a wait the stop does not end fails here rather than holding the suite.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromMinutes(2), Token));
        }

        var entry = Assert.Single(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Folds);
        var path = Assert.Single(entry.Paths);

        Assert.Equal((DssHarness.Core.FileSystem.FileContentHash.Of("two\n"u8), DssHarness.Core.FileSystem.FileContentHash.Of("two\n"u8), (bool?)false), (path.Before, path.Left, path.Changed));
        Assert.Equal(("stopped", "b.txt"), (entry.State, path.Path));

        var committed = await Committing(kit).RunAsync(Run(kit, Commit(), worksIn: null, read: Names("folds", step.Name)), Token);

        Assert.True(committed.Passed, committed.Error);
        Assert.Equal("""{"committed":false,"sha":null,"paths":[],"ignored":[]}""", committed.Output!.ToJsonString());
        Assert.Equal(head, await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token));
        Assert.Equal("two\nformatted\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// A side-task's write that waited on a fold's hold of the main tree - of a fold, and of a deletion that folds, which
    /// lets the tree go before its transcripts, evidence and worktree's removal - lands the moment the fold lets it go,
    /// before the step settles its ledger entry: the entry holds what the fold left, as the fold read it under its hold,
    /// never the side-task's bytes, and the commit after it finds the path changed since, stops for a person and commits
    /// nothing under the fold's message.
    /// </summary>
    [Theory]
    [InlineData("fold")]
    [InlineData("delete-agent")]
    public async Task ASideTasksWriteOnceAFoldLetsTheTreeGo_IsNeverRecordedAsTheFolds_AndItsCommitStopsForAPerson(string type)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var sideTask = new SideTaskWrite(kit.Main, "b.txt", "two\na side-task's edit\n");
        var ledger = new FoldLedger(kit.Harness.OrchestrationStore, new SideTaskFirst(kit.Harness.FileSystem, Path.Combine(kit.Main, "b.txt"), "two\nagent edit\n", sideTask), TimeProvider.System);
        var (runner, step) = type == "fold"
            ? ((IFlowStepTypeRunner)new FoldStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, kit.Harness.AgentService, ledger, kit.Harness.GraphSyncs), Fold())
            : (new DeleteAgentStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, kit.Harness.AgentService, ledger, kit.Harness.GraphSyncs), Delete());
        var head = await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token);

        var ended = await runner.RunAsync(Run(kit, step), Token);

        // Made where the step's ledger never read the path again, once the step is done: after the fold let the tree go
        // either way.
        sideTask.Write();

        Assert.True(ended.Passed, ended.Error);
        Assert.Equal("two\na side-task's edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));

        var path = Assert.Single(Assert.Single(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Folds).Paths);

        Assert.Equal((DssHarness.Core.FileSystem.FileContentHash.Of("two\nagent edit\n"u8), (bool?)true), (path.Left, path.Changed));

        var committed = await Committing(kit).RunAsync(Run(kit, Commit(), worksIn: null, read: Names("folds", step.Name)), Token);

        Assert.Equal(
            (HarnessExit.Refused, $"'b.txt' (folded by '{step.Name}') has changed since; commit or restore it, then resume. Nothing was staged."),
            (committed.Exit, committed.Error));
        Assert.Equal(head, await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token));
        Assert.Empty(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Commits);
    }

    /// <summary>
    /// A fold - and a deletion that folds - whose step crashed once the fold let the main tree go, its verb having recorded
    /// what it left under its hold and nothing recorded after, is settled by the step's next attempt by that record: a
    /// side-task's write let in the moment the fold let go, made before the next attempt, is never taken for the fold's,
    /// nor read from the tree, and the commit after it finds the path changed since and stops for a person.
    /// </summary>
    [Theory]
    [InlineData("fold")]
    [InlineData("delete-agent")]
    public async Task AFoldWhoseStepCrashedOnceItLetTheTreeGo_IsSettledByWhatItRecorded_NeverByASideTasksWrite(string type)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var ledger = Ledger(kit);
        var planned = new AgentPlanned([], [], [], [], null) { Written = ["b.txt"] };
        var (runner, step) = type == "fold" ? ((IFlowStepTypeRunner)Runner(kit), Fold()) : (Deleting(kit), Delete());
        var id = await ledger.IntendAsync(kit.Layout, kit.Main, step.Name, 1, type, Agent, planned, Token);
        var intent = new FoldIntent(ledger, kit.Layout, id);
        var head = await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token);

        // The apply the crashed attempt called, as it called it - what it left recorded under its hold - and nothing
        // recorded after: the step's process ended there.
        var applied = type == "fold"
            ? await kit.Harness.AgentService.FoldAsync(new FoldAgentRequest(kit.Main, OrchestrationKit.Orchestrator, Agent, new FoldAllowances(), Apply: true) { Expected = planned, Intent = intent }, Token)
            : await kit.Harness.AgentService.DeleteAsync(new DeleteAgentRequest(kit.Main, OrchestrationKit.Orchestrator, Agent, new FoldAllowances(), Apply: true, DiscardUncommitted: false) { Expected = planned, Intent = intent }, Token);

        Assert.True(applied.Outcome.Succeeded, applied.Outcome.Message);
        Assert.Equal(FlowFoldEntry.Written, kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Of(id)!.State);

        // A side-task's write that waited on the fold's hold, in before the step's next attempt.
        new SideTaskWrite(kit.Main, "b.txt", "two\na side-task's edit\n").Write();

        var next = await runner.RunAsync(Run(kit, step, attempt: 2), Token);
        var entry = kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Of(id)!;

        Assert.True(next.Passed, next.Error);
        Assert.Equal(
            (FlowFoldEntry.Interrupted, DssHarness.Core.FileSystem.FileContentHash.Of("two\nagent edit\n"u8), (bool?)true, (bool?)null),
            (entry.State, entry.Paths[0].Left, entry.Paths[0].Changed, entry.ReadFromTree));

        var committed = await Committing(kit).RunAsync(Run(kit, Commit(), worksIn: null, read: Names("folds", step.Name)), Token);

        Assert.Equal(
            (HarnessExit.Refused, $"'b.txt' (folded by '{step.Name}') has changed since; commit or restore it, then resume. Nothing was staged."),
            (committed.Exit, committed.Error));
        Assert.Equal(head, await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token));
    }

    /// <summary>
    /// A deletion whose removal throws once its fold let the main tree go - a side-task's write that waited on the fold's
    /// hold in by then - keeps what its fold recorded it left under that hold: the step settles the fold by it as the
    /// throw goes on, never reading the side-task's bytes from the tree, and the commit after it stops for a person.
    /// </summary>
    [Fact]
    public async Task ADeletionWhoseRemovalThrowsOnceItLetTheTreeGo_KeepsWhatItsFoldRecorded_NeverASideTasksWrite()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var sideTask = new SideTaskWrite(kit.Main, "b.txt", "two\na side-task's edit\n");
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            BeforeRun = arguments =>
            {
                if (arguments is ["update-ref", "-d", ..])
                {
                    // The removal forgets the commit the worktree stood on, long after the deletion let the tree go: the
                    // side-task's write that waited on it is in.
                    sideTask.Write();
                    throw new InvalidOperationException("the removal could not be asked of git");
                }
            },
        };
        var agents = kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git);
        var runner = new DeleteAgentStepRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, agents, Ledger(kit), kit.Harness.GraphSyncs);
        var head = await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Run(kit, Delete()), Token));
        var entry = Assert.Single(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Folds);

        Assert.Equal("the removal could not be asked of git", thrown.Message);
        Assert.Equal("two\na side-task's edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Equal(
            (FlowFoldEntry.Interrupted, DssHarness.Core.FileSystem.FileContentHash.Of("two\nagent edit\n"u8), (bool?)true, (bool?)null),
            (entry.State, entry.Paths[0].Left, entry.Paths[0].Changed, entry.ReadFromTree));

        var committed = await Committing(kit).RunAsync(Run(kit, Commit(), worksIn: null, read: Names("folds", "delete")), Token);

        Assert.Equal(
            (HarnessExit.Refused, "'b.txt' (folded by 'delete') has changed since; commit or restore it, then resume. Nothing was staged."),
            (committed.Exit, committed.Error));
        Assert.Equal(head, await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token));
    }

    /// <summary>The <c>waited</c> member of a step's output: what it waited out for the main tree's lock, and how long.</summary>
    private static JsonObject WaitedOf(FlowStepResult ended) => ended.Output![MainTreeWriteLock.WaitedMember]!.AsObject();

    private static WorkflowStep Refresh(params string[] paths) => new() { Name = "refresh", Type = "refresh", Of = "made", Paths = new FlowList([.. paths], null) };

    private static WorkflowStep Rebase() => new() { Name = "rebase", Type = "rebase", Of = "made" };

    private static RebaseStepRunner Rebasing(OrchestrationKit kit)
        => new(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, kit.Harness.AgentService, kit.Harness.GraphSyncs);

    private static WorkflowStep Fold() => new() { Name = "fold", Type = "fold", Of = "made" };

    private static WorkflowStep Delete() => new() { Name = "delete", Type = "delete-agent", Of = "made" };

    private static FoldLedger Ledger(OrchestrationKit kit) => new(kit.Harness.OrchestrationStore, kit.Harness.FileSystem, TimeProvider.System);

    private static WorkflowStep Commit() => new() { Name = "commit", Type = "commit", Message = "checkpoint" };

    /// <summary>The commit step over the run's ledger of its folds, under no isolation.</summary>
    private static CommitStepRunner Committing(OrchestrationKit kit)
        => new(
            kit.Harness.ContextLoader,
            kit.Harness.Platform,
            kit.Harness.OrchestrationStore,
            Ledger(kit),
            kit.Harness.GitClient,
            kit.Harness.GitPublisher,
            kit.Harness.FileSystem,
            new TestIsolation());

    private static FoldStepRunner Runner(OrchestrationKit kit)
        => new(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, kit.Harness.AgentService, Ledger(kit), kit.Harness.GraphSyncs);

    private static DeleteAgentStepRunner Deleting(OrchestrationKit kit)
        => new(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, kit.Harness.AgentService, Ledger(kit), kit.Harness.GraphSyncs);

    private static RefreshStepRunner Refreshing(OrchestrationKit kit)
        => new(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, kit.Harness.AgentService, kit.Harness.GraphSyncs);

    /// <summary>One run of <paramref name="step"/> on <paramref name="kit"/>'s orchestrator, working in <paramref name="worksIn"/> - or on <paramref name="target"/>.</summary>
    private static FlowStepRun Run(
        OrchestrationKit kit,
        WorkflowStep step,
        IReadOnlyList<FlowAnswerGiven>? answers = null,
        int attempt = 1,
        string? worksIn = Agent,
        string? target = null,
        IReadOnlyDictionary<string, FlowValue>? read = null)
        => new(
            step,
            step.Name,
            attempt,
            FlowValue.Missing,
            null,
            null,
            answers ?? [],
            new NoEvents(),
            Place: new FlowRunPlace(kit.Main, OrchestrationKit.Orchestrator, null, Definition),
            Read: read ?? new Dictionary<string, FlowValue>(),
            WorksIn: worksIn,
            Target: target);

    /// <summary>What the engine read where the step started: the names <paramref name="names"/> under <paramref name="key"/>.</summary>
    private static Dictionary<string, FlowValue> Names(string key, params string[] names)
        => new() { [key] = FlowValue.Of(new JsonArray([.. names.Select(name => (JsonNode?)name)])) };

    /// <summary>An answer of a verb, its facts carried.</summary>
    private static UseCaseResult<AgentVerbFacts> Answer(
        int code,
        string summary,
        RequestStanding? standing,
        ConsentKind[] needs,
        RefusalKind[] refusals,
        OutcomeCause cause = OutcomeCause.None,
        AgentPlanned? planned = null)
    {
        var outcome = CommandOutcome.Failed(code, summary);

        return UseCaseResult<AgentVerbFacts>.From(outcome with
        {
            Cause = cause,
            Facts = new AgentVerbFacts(code, summary, false, standing, null, [], needs, refusals, Planned: planned ?? new AgentPlanned([], [], [], [], null)),
        });
    }

    /// <summary>
    /// An agent service whose fold answers <paramref name="dry"/> to every dry run and each of <paramref name="applies"/> in
    /// turn to an apply, the last again once they run out - an apply that goes through recording its write through the
    /// run's ledger under the main tree's lock, as the verb does, having written nothing.
    /// </summary>
    private static IAgentService Scripted(UseCaseResult<AgentVerbFacts> dry, params UseCaseResult<AgentVerbFacts>[] applies)
    {
        var agents = Substitute.For<IAgentService>();
        var at = 0;

        agents.FoldAsync(Arg.Any<FoldAgentRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.Arg<FoldAgentRequest>();

                if (!request.Apply)
                {
                    return dry;
                }

                var answer = applies[Math.Min(at++, applies.Length - 1)];

                if (request.Intent is { } intent && answer.Outcome.ExitCode == HarnessExit.Success)
                {
                    await OrchestrationKit.WrittenAsync(intent.Ledger, intent.Layout, request.StartDirectory, intent.Id);
                }

                return answer;
            });

        return agents;
    }

    private static List<string> Texts(JsonNode? array) => [.. array!.AsArray().Select(item => item!.GetValue<string>())];

    private sealed class NoEvents : IFlowRunEvents
    {
        public void LegPlaced(string leg, bool inACopy) { }

        public void EveryLegPlaced() { }

        public void LegCopyMade(string leg) { }

        public void LegEnded(string leg) { }

        public void Used(long tokens, double? usd, string? uncounted = null) { }

        public void HandoffTaken() { }
    }

    /// <summary>
    /// A side-task's write of one path of the main tree, made once, under the tree's lock as every side-task's write is:
    /// what a test lets in the moment a fold has let the tree go.
    /// </summary>
    private sealed class SideTaskWrite(string main, string path, string text)
    {
        private int _written;

        /// <summary>Makes the write, waiting for the tree's lock.</summary>
        public void Write()
        {
            if (Interlocked.Exchange(ref _written, 1) != 0)
            {
                return;
            }

            using var hold = MainTreeWriteLock.HoldAsync(main, TestContext.Current.CancellationToken).GetAwaiter().GetResult()
                ?? throw new OperationCanceledException("the test was stopped while its side-task waited for the main tree");

            OrchestrationKit.Write(main, path, text);
        }

        /// <summary>Makes the write where nobody holds the tree's lock - a fold holding it lets it in only once it lets go.</summary>
        public void WriteIfLetIn()
        {
            if (Volatile.Read(ref _written) != 0)
            {
                return;
            }

            using var hold = MachineWideMutex.TryHold(MainTreeWriteLock.NameFor(main), "a test as a side-task");

            if (hold is not null && Interlocked.Exchange(ref _written, 1) == 0)
            {
                OrchestrationKit.Write(main, path, text);
            }
        }
    }

    /// <summary>
    /// The real file system, except that looking at <paramref name="path"/> once it holds <paramref name="folded"/> - what
    /// the fold wrote there - first lets <paramref name="sideTask"/>'s write in where the fold has let the tree go, as a
    /// side-task's write that waited on the fold's hold is let in the moment it does: whatever reads the path after the
    /// fold let go reads the side-task's.
    /// </summary>
    private sealed class SideTaskFirst(DssHarness.Core.FileSystem.IFileSystem inner, string path, string folded, SideTaskWrite sideTask) : PassThroughFileSystem(inner)
    {
        public override DssHarness.Core.FileSystem.PathKind KindOf(string asked)
        {
            if (string.Equals(Path.GetFullPath(asked), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && File.Exists(path) && File.ReadAllText(path) == folded)
            {
                sideTask.WriteIfLetIn();
            }

            return base.KindOf(asked);
        }
    }

    /// <summary>The real file system, except that one file cannot be read while <see cref="Unreadable"/> says so, as while another program holds it.</summary>
    private sealed class UnreadableWhenAsked(DssHarness.Core.FileSystem.IFileSystem inner, string held) : PassThroughFileSystem(inner)
    {
        public const string Message = "the file is held by another program";

        public bool Unreadable { get; set; }

        public override Stream OpenRead(string path)
            => Unreadable && string.Equals(Path.GetFullPath(path), Path.GetFullPath(held), StringComparison.OrdinalIgnoreCase)
                ? throw new IOException(Message)
                : base.OpenRead(path);
    }
}
