using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.Configuration;
using DssHarness.Core.Repository;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
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
    /// The ledger settles each fold as it ended: an apply that stopped part way (21) stopped, with what it left read from
    /// the tree; one that threw after its intent was written interrupted, before the exception goes on; and an intent a
    /// crash left - nothing written after it - interrupted where the next attempt starts, before it folds anything.
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
        Assert.Empty(FoldLedger.Uncommitted(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)));
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
    /// path to within, and takes no allowance, whatever its allow names; an intent a crash left after the closing is
    /// settled all the same, from what the tree holds, and the run's graph's base is synced by what it changed.
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

    private static WorkflowStep Fold() => new() { Name = "fold", Type = "fold", Of = "made" };

    private static WorkflowStep Delete() => new() { Name = "delete", Type = "delete-agent", Of = "made" };

    private static FoldLedger Ledger(OrchestrationKit kit) => new(kit.Harness.OrchestrationStore, kit.Harness.FileSystem, TimeProvider.System);

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

    /// <summary>An agent service whose fold answers <paramref name="dry"/> to every dry run and each of <paramref name="applies"/> in turn to an apply, the last again once they run out.</summary>
    private static IAgentService Scripted(UseCaseResult<AgentVerbFacts> dry, params UseCaseResult<AgentVerbFacts>[] applies)
    {
        var agents = Substitute.For<IAgentService>();
        var at = 0;

        agents.FoldAsync(Arg.Any<FoldAgentRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<FoldAgentRequest>().Apply ? applies[Math.Min(at++, applies.Length - 1)] : dry));

        return agents;
    }

    private static List<string> Texts(JsonNode? array) => [.. array!.AsArray().Select(item => item!.GetValue<string>())];

    private sealed class NoEvents : IFlowRunEvents
    {
        public void LegPlaced(string leg, bool inACopy) { }

        public void EveryLegPlaced() { }

        public void LegCopyMade(string leg) { }

        public void LegEnded(string leg) { }

        public void Used(long tokens, double usd) { }

        public void HandoffTaken() { }
    }
}
