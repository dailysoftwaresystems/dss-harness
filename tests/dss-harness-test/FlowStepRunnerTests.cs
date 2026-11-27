using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using DssHarness.Core.Ci;
using DssHarness.Core.Git;

namespace DssHarness.Tests;

/// <summary>
/// The machine step runners of slice 20a, each over a real repository: orient's facts, the plan check and the machine's
/// check of a plan's work items, and create-agent's making of the agent the engine named - none, whole, part made with
/// and without its worktree's commit, deleted, and one that only reads - and the dispatcher that serves each type it has a
/// runner for and refuses the rest.
/// </summary>
public sealed class FlowStepRunnerTests : IDisposable
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

    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    private static readonly WorkflowDefinition Flow = Parse("""
        name: flow
        steps:
          - name: orient
            type: orient
        """);

    /// <summary>A repository with the harness initialised by <paramref name="config"/>, committed, on branch 'work'.</summary>
    private async Task<(HarnessFactory Harness, TempDirectory Repository)> RepositoryAsync(HarnessConfig? config = null)
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

    private static OrientRunner Orient(HarnessFactory harness)
        => new(harness.ContextLoader, harness.Platform, harness.GitClient, harness.FileSystem, harness.OrchestrationStore, harness.WorktreeService, harness.RunLock, new TreeHolders(harness.RunLock, harness.Slots, harness.Identity));

    private static CreateAgentRunner Create(HarnessFactory harness)
        => new(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.AgentService);

    private static FlowStepRun Run(WorkflowStep step, string repository, IReadOnlyDictionary<string, FlowValue>? read = null, FlowMadeAgent? made = null, string run = "r1", WorkflowDefinition? definition = null, string? startCommit = null)
        => new(step, step.Name, 1, FlowValue.Missing, null, null, [], new NoEvents(), Place: new FlowRunPlace(repository, run, startCommit, definition ?? Flow), Read: read ?? new Dictionary<string, FlowValue>(), Made: made);

    private sealed class NoEvents : IFlowRunEvents
    {
        public void LegPlaced(string leg, bool inACopy) { }

        public void EveryLegPlaced() { }

        public void LegCopyMade(string leg) { }

        public void LegEnded(string leg) { }

        public void Used(FlowTurnSpend turn) { }

        public void Held(IReadOnlyList<string> on, IReadOnlyList<QueueAhead> ahead) { }

        public void HandoffTaken() { }
    }

    private static readonly WorkflowStep OrientStep = new() { Name = "orient", Type = "orient" };

    /// <summary>
    /// Orient answers what the repository is: its branch, the commit the run started on, no remote and so no base branch
    /// and no CI to read, each leg and how it tests - the exclusions its args apply read as ctest reads them, in any
    /// spelling and form, the last given alone, split where the invocation joins several - what is dirty, nothing open
    /// and nothing in flight.
    /// </summary>
    [Fact]
    public async Task Orient_AnswersWhatTheRepositoryIs()
    {
        var platform = new HostPlatform();
        var (harness, repository) = await RepositoryAsync(new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs =
            {
                ["native"] = new LegConfig
                {
                    Os = platform.PlatformKey,
                    Processor = platform.Processor,
                    Config = "debug",
                    Test = new TestConfig
                    {
                        All = new TestInvocation
                        {
                            Runner = "ctest",
                            Args = ["--output-on-failure", "-E", "slow", "--exclude-regex=gpu|net"],
                            FilterArg = "-R",
                            FilterJoin = "|",
                            ExcludeArg = "-E",
                            ExcludeJoin = "|",
                            SuccessPattern = "passed",
                            RemoteExcludes = ["needs-display"],
                        },
                    },
                },
                ["silent"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug" },
            },
            Ci = new CiSettings { LegJobPattern = "build-(?<leg>.+)", BuildStep = "Build", TestStep = "Test" },
        });
        repository.WriteFile("loose.txt", "loose\n");

        var result = await Orient(harness).RunAsync(Run(OrientStep, repository.Path, startCommit: "0123abcd"), Token);

        Assert.True(result.Passed, result.Error);
        var output = result.Output!.AsObject();
        Assert.Equal("work", (string?)output["branch"]);
        Assert.Null(output["baseBranch"]);
        Assert.Equal("0123abcd", (string?)output["startCommit"]);
        Assert.False((bool)output["hasRemote"]!);
        Assert.False((bool)output["onForge"]!);
        Assert.True((bool)output["hasLegs"]!);
        Assert.Equal(2, (int)output["legCount"]!);
        Assert.False((bool)output["anchors"]!);

        // CI is declared, and there is no branch to read it on.
        Assert.False((bool)output["ciConfigured"]!);
        Assert.False((bool)output["hasMutations"]!);
        Assert.Equal(
            """[{"name":"native","program":"ctest","filterArg":"-R","exclude":["gpu","net"],"remoteExcludes":["needs-display"]}]""",
            output["tests"]!.ToJsonString());
        Assert.Equal("""["loose.txt"]""", output["dirtyPaths"]!.ToJsonString());
        Assert.Equal("[]", output["openAgents"]!.ToJsonString());
        Assert.Equal("[]", output["inFlight"]!.ToJsonString());
        Assert.Equal(output.Select(member => member.Key).Order(StringComparer.Ordinal), new[] { "anchors", "baseBranch", "branch", "ciConfigured", "dirtyPaths", "hasLegs", "hasMutations", "hasRemote", "inFlight", "legCount", "onForge", "openAgents", "startCommit", "tests" });
    }

    /// <summary>
    /// With a remote, the base branch is the pull request's base where the engine filled one, else the remote's default;
    /// CI declared is configured only where that remote names a repository on a forge, which a repository on this machine
    /// does not, and CI is read through the forge; anchors, mutation arms, the open agents of every run and who holds the main tree
    /// are each said; the run's records naming no start commit, HEAD is the one.
    /// </summary>
    [Fact]
    public async Task Orient_SaysTheBaseBranch_TheOpenAgents_AndWhoHoldsTheTree()
    {
        var (harness, repository) = await RepositoryAsync(new HarnessConfig
        {
            Ci = new CiSettings { LegJobPattern = "build-(?<leg>.+)", BuildStep = "Build", TestStep = "Test" },
            Anchors = new AnchorSettings { CitationRoots = ["src"] },
            Mutations = new MutationSettings { Registry = "arms.md" },
        });
        using var remote = new TempDirectory();
        repository.WriteFile("arms.md", "# arms\n");
        await harness.RunGitAsync(remote.Path, ["init", "--quiet", "--bare", "."], Token);
        await harness.RunGitAsync(repository.Path, ["remote", "add", "origin", remote.Path], Token);
        await harness.RunGitAsync(repository.Path, ["push", "--quiet", "origin", "work:trunk"], Token);
        await harness.RunGitAsync(repository.Path, ["remote", "set-head", "origin", "trunk"], Token);

        var context = await harness.ContextLoader.LoadAsync(repository.Path, Token);
        var other = OrchestratorLayout.Of(context.Layout, "r9");
        harness.FileSystem.CreateDirectory(other.Directory);
        harness.OrchestrationStore.WriteOrchestrator(other, new OrchestratorRecord { Kind = OrchestratorRecord.KindName, Name = "r9", Model = "flow-engine", Parallel = 2, CreatedAt = DateTimeOffset.UnixEpoch });
        harness.OrchestrationStore.WriteAgent(other, new AgentRecord { Kind = AgentRecord.KindName, Name = "a4", Orchestrator = "r9", Model = "m", CreatedAt = DateTimeOffset.UnixEpoch, Worktree = WorktreeAddress.Nested("r9", "a4").Name, WorktreesRoot = context.Config.Worktrees.Root, State = AgentStates.Live });
        harness.OrchestrationStore.WriteAgent(other, new AgentRecord { Kind = AgentRecord.KindName, Name = "a5", Orchestrator = "r9", Model = "m", CreatedAt = DateTimeOffset.UnixEpoch, Access = AgentRecord.AccessRead, Worktree = WorktreeAddress.Nested("r9", "a5").Name, Tree = AgentRecord.TreeRepository, WorktreesRoot = context.Config.Worktrees.Root, State = AgentStates.Live });

        var held = await harness.RunLock.TryAcquireAsync(
            context.Layout,
            new LockRequest { Host = HostId.Local.ToString(), Tree = harness.RunLock.Key(HostId.Local.ToString(), repository.Path), Scope = LockScope.TreeExclusive, RunId = RunId.New(), Command = "build" },
            Token);
        await using var handle = held.Handle!;

        var head = (await harness.RunGitAsync(repository.Path, ["rev-parse", "HEAD"], Token)).StandardOutput.Trim();
        var fromRemote = await Orient(harness).RunAsync(Run(OrientStep, repository.Path), Token);
        var fromPullRequest = await Orient(harness).RunAsync(Run(OrientStep, repository.Path, new Dictionary<string, FlowValue> { ["base"] = FlowValue.Of("release") }), Token);
        await harness.RunGitAsync(repository.Path, ["remote", "set-url", "origin", "https://github.com/acme/widgets.git"], Token);
        var onForge = await Orient(harness).RunAsync(Run(OrientStep, repository.Path), Token);

        Assert.True(fromRemote.Passed, fromRemote.Error);
        var output = fromRemote.Output!.AsObject();
        Assert.Equal("trunk", (string?)output["baseBranch"]);
        Assert.Equal(head, (string?)output["startCommit"]);
        Assert.True((bool)output["hasRemote"]!);
        Assert.False((bool)output["onForge"]!);
        Assert.False((bool)output["ciConfigured"]!);
        Assert.True((bool)onForge.Output!["onForge"]!);
        Assert.True((bool)onForge.Output!["ciConfigured"]!);
        Assert.Equal("trunk", (string?)onForge.Output!["baseBranch"]);
        Assert.True((bool)output["anchors"]!);
        Assert.True((bool)output["hasMutations"]!);
        Assert.Equal("""[{"run":"r9","agent":"a4"}]""", output["openAgents"]!.ToJsonString());
        Assert.Contains("running 'build'", (string?)output["inFlight"]![0], StringComparison.Ordinal);
        Assert.Equal("release", (string?)fromPullRequest.Output!["baseBranch"]);
    }

    /// <summary>Orient on a detached HEAD fails, refused, saying a run commits on a branch.</summary>
    [Fact]
    public async Task Orient_OnADetachedHead_IsRefused()
    {
        var (harness, repository) = await RepositoryAsync();
        await harness.RunGitAsync(repository.Path, ["checkout", "--quiet", "--detach"], Token);

        var result = await Orient(harness).RunAsync(Run(OrientStep, repository.Path), Token);

        Assert.False(result.Passed);
        Assert.Equal(HarnessExit.Refused, result.Exit);
        Assert.Equal("the main checkout's HEAD is detached, and a run commits its work on a branch: check out the run's branch again", result.Error);
    }

    /// <summary>
    /// Orient says only what is so: CI not declared is not configured, though there is a base branch to read it on; a
    /// citation root that names nothing once read is no root, as the citation check reads it; and an agent is open
    /// where git lists its worktree, with no record of it.
    /// </summary>
    [Fact]
    public async Task Orient_SaysOnlyWhatIsSo()
    {
        var (harness, repository) = await RepositoryAsync(new HarnessConfig { Anchors = new AnchorSettings { CitationRoots = ["."] } });
        var context = await harness.ContextLoader.LoadAsync(repository.Path, Token);
        var other = OrchestratorLayout.Of(context.Layout, "r9");
        harness.FileSystem.CreateDirectory(other.Directory);
        harness.OrchestrationStore.WriteOrchestrator(other, new OrchestratorRecord { Kind = OrchestratorRecord.KindName, Name = "r9", Model = "flow-engine", Parallel = 2, CreatedAt = DateTimeOffset.UnixEpoch });
        var listed = WorktreeAddress.Nested("r9", "a6").PathUnder(context.Layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root));
        await harness.RunGitAsync(repository.Path, ["worktree", "add", "--quiet", "-b", "listed", listed], Token);

        var result = await Orient(harness).RunAsync(Run(OrientStep, repository.Path, new Dictionary<string, FlowValue> { ["base"] = FlowValue.Of("release") }), Token);

        Assert.True(result.Passed, result.Error);
        var output = result.Output!.AsObject();
        Assert.Equal("release", (string?)output["baseBranch"]);
        Assert.False((bool)output["ciConfigured"]!);
        Assert.False((bool)output["anchors"]!);
        Assert.Equal("""[{"run":"r9","agent":"a6"}]""", output["openAgents"]!.ToJsonString());
    }

    /// <summary>Orient fails where the branch names no commit yet, refused, and where who holds the main tree cannot be read.</summary>
    [Fact]
    public async Task Orient_OnABranchWithNoCommit_OrALockItCannotRead_Fails()
    {
        var (harness, repository) = await RepositoryAsync();
        var lockFile = new HarnessLayout(repository.Path, repository.Path).LockFile;
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        File.WriteAllText(lockFile, "not the lock");

        var unread = await Orient(harness).RunAsync(Run(OrientStep, repository.Path), Token);

        Assert.False(unread.Passed);
        Assert.Equal(HarnessExit.CommandFailed, unread.Exit);
        Assert.StartsWith("who holds the main tree could not be read: ", unread.Error, StringComparison.Ordinal);

        File.Delete(lockFile);
        await harness.RunGitAsync(repository.Path, ["checkout", "--quiet", "--orphan", "fresh"], Token);

        var unborn = await Orient(harness).RunAsync(Run(OrientStep, repository.Path), Token);

        Assert.False(unborn.Passed);
        Assert.Equal(HarnessExit.Refused, unborn.Exit);
        Assert.Equal("the main checkout's branch 'fresh' names no commit yet, and a run starts from one: commit first", unborn.Error);
    }

    /// <summary>A step of a run held in memory alone has no repository to work in: the runner refuses it as the engine's fault.</summary>
    [Fact]
    public async Task AStepOfARunHeldInMemory_IsRefused()
    {
        var harness = new HarnessFactory();
        var run = new FlowStepRun(OrientStep, "orient", 1, FlowValue.Missing, null, null, [], new NoEvents());

        var refused = await Assert.ThrowsAsync<HarnessException>(() => Orient(harness).RunAsync(run, Token));

        Assert.Equal(HarnessExit.InternalError, refused.ExitCode);
        Assert.Equal("'orient' is a orient step of a run held in memory alone, which has no repository for it to work in", refused.Message);
    }

    /// <summary>The dispatcher serves each type by its runner and refuses one no runner serves; the tool registers a runner for each type it serves, and none other.</summary>
    [Fact]
    public async Task TheDispatcher_ServesTheTypesItHasRunnersFor_AndRefusesTheRest()
    {
        var (harness, repository) = await RepositoryAsync();
        var runners = new FlowStepRunners([Orient(harness)], harness.GraphSyncs);

        Assert.True((await runners.RunAsync(Run(OrientStep, repository.Path), Token)).Passed);

        var build = await runners.RunAsync(Run(new WorkflowStep { Name = "build", Type = "build" }, repository.Path), Token);
        Assert.Equal(HarnessExit.ToolMissing, build.Exit);
        Assert.StartsWith("no runner serves a step of type 'build' yet", build.Error, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new FlowStepRunners([Orient(harness), Orient(harness)], harness.GraphSyncs));

        using var provider = TestRegistrations.Of(new SessionOptions { Command = "test" }, OperationId.New())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.Equal(FlowStepRunners.Served, provider.GetServices<IFlowStepTypeRunner>().Select(runner => runner.Type));
        Assert.Contains(
            $"the steps `FlowStepRunners.Served` lists are served - {string.Join(", ", FlowStepRunners.Served.Select(type => $"`{type}`"))} - and a step of any other type fails until its runner lands",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);

        // The README counts them in words and names each.
        var readme = Prose.Document("README.md");
        var served = readme.IndexOf($"{Spelled(FlowStepRunners.Served.Count)} steps are served so far:", StringComparison.Ordinal);

        Assert.True(served >= 0, "the README counts the steps served");
        var listed = readme[served..readme.IndexOf("A check that finds red", served, StringComparison.Ordinal)];
        Assert.All(FlowStepRunners.Served, type => Assert.Contains($"`{type}`", listed, StringComparison.Ordinal));
        Assert.Contains(
            $"else its own ({ReportText.Limit(GhProcess.Limit)} for `gh` and a push, {ReportText.Limit(PublisherCommands.CommitLimit)} for a commit's)",
            readme,
            StringComparison.Ordinal);
        Assert.Equal(GhProcess.Limit, PublisherCommands.PushLimit);
        Assert.IsType<FlowStepRunners>(provider.GetRequiredService<IFlowStepRunner>());
    }

    /// <summary>A count below a hundred as the README writes it at a sentence's start: "Twenty-one".</summary>
    private static string Spelled(int count)
    {
        string[] ones = ["Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"];
        string[] tens = ["Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];
        Assert.InRange(count, 0, 99);
        return count < 20 ? ones[count] : count % 10 == 0 ? tens[(count / 10) - 2] : $"{tens[(count / 10) - 2]}-{ones[count % 10].ToLowerInvariant()}";
    }

    /// <summary>
    /// A reason step's start syncs its view through the dispatcher before its runner is asked - its agent's worktree's, none
    /// for an agent that only reads, whose own tree's view is synced once its runner readied the tree, and none for an agent
    /// the run has no record of - where the run has a graph, each a line of the sync log naming the step's start; a run with
    /// no graph has none made.
    /// </summary>
    [Fact]
    public async Task AReasonStepsStart_SyncsItsView_WhereTheRunHasAGraph()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        await kit.CreateReadingAgentAsync("reader");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var runners = new FlowStepRunners([], kit.Harness.GraphSyncs);

        var none = await runners.RunAsync(Reason(kit, "ag"), Token);

        Assert.Equal(HarnessExit.ToolMissing, none.Exit);
        Assert.False(File.Exists(kit.Layout.GraphManifestFile));

        await kit.Harness.CodebaseGraph.SyncAsync(GraphRequest(kit), Token);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nby hand\n");

        await runners.RunAsync(Reason(kit, "ag"), Token);
        await runners.RunAsync(Reason(kit, "reader"), Token);

        // An agent the run has no record of has no view to sync.
        await runners.RunAsync(Reason(kit, "nobody"), Token);

        var lines = File.ReadAllLines(kit.Layout.GraphSyncFile).Select(line => JsonNode.Parse(line)!).Select(line => ((string?)line["view"], (string?)line["why"]));

        Assert.Equal([("base", "test"), ("ag", "think:start")], lines);
        Assert.Contains("b.txt", kit.Harness.OrchestrationStore.ReadGraphView(kit.Layout, "ag")!.Files.Keys);

        // The reader's start synced nothing - the base among it, which is not the tree it reads: the person's edit of the
        // main checkout is in no record yet.
        Assert.Equal(
            (await kit.Harness.RunGitAsync(kit.Main, ["rev-parse", "HEAD:a.txt"], Token)).StandardOutput.Trim(),
            kit.Harness.OrchestrationStore.ReadGraphManifest(kit.Layout)!.Files["a.txt"].Blob);
        Assert.False(File.Exists(kit.Layout.GraphViewFile("reader")));
    }

    /// <summary>
    /// A reason step whose agent only reads is synced nothing at its start - its tree stands as the agent's last step left
    /// it until the step's runner brings it to the main checkout's HEAD, or to the commit the step names - and its own
    /// tree's view once the tree is readied there, a line of the sync log naming the agent, whether its step names a commit
    /// or not; never the base, which its tree is not. A step of another type that works as the reader is synced the base
    /// at its start, for no runner of its readies the tree. Red with a reader whose step names no commit served the base.
    /// </summary>
    [Fact]
    public async Task AReadersOwnTreesView_IsSyncedOnlyOnceItsTreeIsReadied_WhetherItsStepNamesACommitOrNot()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        await kit.Harness.CodebaseGraph.SyncAsync(GraphRequest(kit), Token);
        var start = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;
        var plain = Reason(kit, "reader");
        var atCommit = plain with { Step = plain.Step with { Commit = start } };
        var syncs = kit.Harness.GraphSyncs;

        IReadOnlyList<(string?, string?)> Lines()
            => [.. File.ReadAllLines(kit.Layout.GraphSyncFile).Select(line => JsonNode.Parse(line)!).Select(line => ((string?)line["view"], (string?)line["why"]))];

        await syncs.AtStartAsync(plain, Token);
        await syncs.AtStartAsync(atCommit, Token);

        Assert.Equal([("base", "test")], Lines());
        Assert.False(File.Exists(kit.Layout.GraphViewFile("reader")));

        await syncs.ReadiedAsync(plain, "reader", Token);

        Assert.Equal([("base", "test"), ("reader", "think:start")], Lines());
        Assert.True(File.Exists(kit.Layout.GraphViewFile("reader")));
        PathAssert.Same(kit.Record("reader").WorktreePath(new DssHarness.Core.Repository.HarnessLayout(kit.Main, kit.Main))!, kit.Harness.OrchestrationStore.ReadGraphView(kit.Layout, "reader")!.Tree);

        await syncs.ReadiedAsync(atCommit, "reader", Token);

        Assert.Equal([("base", "test"), ("reader", "think:start"), ("reader", "think:start")], Lines());

        // A closure check working as the reader readies no tree: its start syncs the base.
        await syncs.AtStartAsync(plain with { Step = new WorkflowStep { Name = "close", Type = "class-closure" }, Instance = "close" }, Token);

        Assert.Equal([("base", "test"), ("reader", "think:start"), ("reader", "think:start"), ("base", "close:start")], Lines());
    }

    /// <summary>
    /// A sync the engine makes of its own that fails never fails its step: it is a line of the sync log saying why, and a
    /// warning - and where the run has no graph, none is tried.
    /// </summary>
    [Fact]
    public async Task AnEngineSyncThatFails_IsSaid_AndItsStepGoesOn()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var graph = Substitute.For<DssHarness.Core.Codebase.ICodebaseGraphService>();
        var output = Substitute.For<IHarnessOutput>();
        graph.SyncAsync(Arg.Any<DssHarness.Core.Codebase.CodebaseGraphSync>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DssHarness.Core.Codebase.CodebaseGraphSynced>(new HarnessException(HarnessExit.ToolMissing, "git did not answer.")));
        var syncs = new FlowGraphSyncs(graph, kit.Harness.OrchestrationStore, new FoldLedger(kit.Harness.OrchestrationStore, kit.Harness.FileSystem, TimeProvider.System), kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.FileSystem, output, TimeProvider.System);

        await syncs.AtStartAsync(Reason(kit, null), Token);

        _ = await graph.DidNotReceive().SyncAsync(Arg.Any<DssHarness.Core.Codebase.CodebaseGraphSync>(), Arg.Any<CancellationToken>());

        await kit.Harness.CodebaseGraph.SyncAsync(GraphRequest(kit), Token);
        await syncs.AtStartAsync(Reason(kit, null), Token);

        var line = JsonNode.Parse(File.ReadLines(kit.Layout.GraphSyncFile).Last())!;

        Assert.Equal(("base", "think:start", "git did not answer."), ((string?)line["view"], (string?)line["why"], (string?)line["failed"]));
        output.Received(1).Warn("reason", $"the codebase graph's view 'base' of run '{OrchestrationKit.Orchestrator}' was not synced (think:start): git did not answer. The step goes on; the next sync of that view brings it up to date.");
    }

    /// <summary>
    /// Whatever fails in a sync the engine makes of its own but a stop - a defect of the graph's as much as git - is said
    /// and never fails its step, and a stop stops it; a reason step whose repository does not read where it starts is said
    /// with no line, its step left to find that out as its runner does.
    /// </summary>
    [Fact]
    public async Task AnEngineSyncThatFailsAnyWayButAStop_IsSaid_AndAStopStopsIt()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var graph = Substitute.For<DssHarness.Core.Codebase.ICodebaseGraphService>();
        var output = Substitute.For<IHarnessOutput>();
        var syncs = new FlowGraphSyncs(graph, kit.Harness.OrchestrationStore, new FoldLedger(kit.Harness.OrchestrationStore, kit.Harness.FileSystem, TimeProvider.System), kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.FileSystem, output, TimeProvider.System);
        await kit.Harness.CodebaseGraph.SyncAsync(GraphRequest(kit), Token);
        graph.SyncAsync(Arg.Any<DssHarness.Core.Codebase.CodebaseGraphSync>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DssHarness.Core.Codebase.CodebaseGraphSynced>(new InvalidOperationException("a defect of the graph's.")));

        await syncs.AtStartAsync(Reason(kit, null), Token);

        output.Received(1).Warn("reason", $"the codebase graph's view 'base' of run '{OrchestrationKit.Orchestrator}' was not synced (think:start): a defect of the graph's. The step goes on; the next sync of that view brings it up to date.");

        graph.SyncAsync(Arg.Any<DssHarness.Core.Codebase.CodebaseGraphSync>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DssHarness.Core.Codebase.CodebaseGraphSynced>(new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(() => syncs.AtStartAsync(Reason(kit, null), Token));

        var lines = File.ReadAllLines(kit.Layout.GraphSyncFile).Length;
        var nowhere = Reason(kit, null) with { Place = new FlowRunPlace(Path.Combine(temp.Path, "gone"), OrchestrationKit.Orchestrator, null, Flow) };

        await syncs.AtStartAsync(nowhere, Token);

        output.Received(1).Warn("reason", Arg.Is<string>(text => text.StartsWith($"the codebase graph's view 'base' of run '{OrchestrationKit.Orchestrator}' was not synced (think:start): ", StringComparison.Ordinal)
            && text.EndsWith("The step goes on; the next sync of that view brings it up to date.", StringComparison.Ordinal)
            && !text.Contains("a defect", StringComparison.Ordinal)));
        Assert.Equal(lines, File.ReadAllLines(kit.Layout.GraphSyncFile).Length);
    }

    /// <summary>A reason step <c>think</c> of the kit's run, working in <paramref name="worksIn"/>.</summary>
    private static FlowStepRun Reason(OrchestrationKit kit, string? worksIn)
        => new(
            new WorkflowStep { Name = "think", Type = "reason" },
            "think",
            1,
            FlowValue.Missing,
            null,
            null,
            [],
            new NoEvents(),
            Place: new FlowRunPlace(kit.Main, OrchestrationKit.Orchestrator, null, Flow),
            Read: new Dictionary<string, FlowValue>(),
            WorksIn: worksIn);

    /// <summary>A whole sync of the base of the kit's run.</summary>
    private static DssHarness.Core.Codebase.CodebaseGraphSync GraphRequest(OrchestrationKit kit)
        => new(new HarnessLayout(kit.Main, kit.Main), kit.Layout, DssHarness.Core.Codebase.CodebaseGraphShape.Default, OrchestrationKit.Settings.Root, Rebuild: false, "test");

    private static PlanValidator Validator(HarnessFactory harness) => new(harness.FileSystem, harness.PathBudget, harness.Platform);

    /// <summary>A work item that owns, reuses and creates as it should.</summary>
    private static JsonObject Item(string name, string owned = "src/one/**", string? reuses = "file.txt")
        => new()
        {
            ["name"] = name,
            ["title"] = "t",
            ["ownedPaths"] = new JsonArray(owned),
            ["forbiddenPaths"] = new JsonArray("src/shared/**"),
            ["reuses"] = reuses is null ? new JsonArray() : new JsonArray(new JsonObject { ["path"] = reuses, ["what"] = "it" }),
            ["creates"] = new JsonArray(),
            ["replaces"] = new JsonArray(),
            ["tests"] = new JsonArray("OneTests"),
            ["noTests"] = "",
            ["classes"] = new JsonArray(),
            ["noClass"] = "the item decides nothing: it carries a document",
        };

    private static async Task<IReadOnlyList<string>> CheckAsync(HarnessFactory harness, TempDirectory repository, params JsonNode?[] items)
        => Validator(harness).Check(items, await harness.ContextLoader.LoadAsync(repository.Path, Token), "r1", Flow, await harness.StartTreeAsync(repository.Path, Token));

    /// <summary>A plan whose items each own their own paths, reuse what is there and name what they create passes.</summary>
    [Fact]
    public async Task ASoundPlan_Passes()
    {
        var (harness, repository) = await RepositoryAsync();
        var creates = Item("two", "src/two/**", reuses: null);
        creates["creates"] = new JsonArray(new JsonObject { ["what"] = "a parser", ["why"] = "none parses it", ["searched"] = "src/" });

        Assert.Empty(await CheckAsync(harness, repository, Item("one"), creates));
    }

    /// <summary>
    /// A plan holds as many work items as the work needs: thirty, each owning its own paths, pass the check, for no count of
    /// items is refused - each is sized to one builder's cycle by the plan, and a round runs them in waves of the run's open
    /// agents.
    /// </summary>
    [Fact]
    public async Task AThirtyItemPlan_PassesTheCheck()
    {
        var (harness, repository) = await RepositoryAsync();

        Assert.Empty(await CheckAsync(harness, repository, [.. Enumerable.Range(1, 30).Select(at => (JsonNode?)Item($"item-{at}", $"src/{at}/**"))]));
    }

    /// <summary>Each check of a list, worded once: none, the wrong shape, a name, a name twice, a path owned twice, a forbidden path owned, the lists.</summary>
    [Fact]
    public async Task EachBreachOfAPlan_IsAProblem()
    {
        var (harness, repository) = await RepositoryAsync();

        Assert.Equal([PlanValidator.NoItems], await CheckAsync(harness, repository));

        var shapeless = Item("shapeless");
        shapeless.Remove("reuses");
        shapeless["forbiddenPaths"] = "src";
        Assert.Equal(
            [
                "work item 1 is of the wrong shape: it is no object, and a work item is one, with its name, the paths it owns and must not touch, and what it reuses, creates and replaces",
                "work item 2 is of the wrong shape: it has no name, and a work item is known by its name",
                "work item 'shapeless' is of the wrong shape: forbiddenPaths, reuses are not there as lists, and each is part of what a work item is",
            ],
            await CheckAsync(harness, repository, JsonValue.Create("item"), new JsonObject { ["name"] = " " }, shapeless));

        Assert.Equal(
            [
                "work item 'Bad--Name' is not named in lowercase letters, digits and single hyphens, as a work item is",
                "2 work items are named 'twice', and a work item is known by its name: name each once",
                "work items 'twice' and 'twice' both own what 'src/one/**' and 'src/one/**' match: no file is written by two items, so give it to one",
            ],
            await CheckAsync(harness, repository, Item("Bad--Name", "src/x/**"), Item("twice"), Item("twice")));

        var greedy = Item("greedy", "src/**");
        Assert.Equal(
            [
                "work items 'greedy' and 'one' both own what 'src/**' and 'src/one/**' match: no file is written by two items, so give it to one",
                "work item 'greedy' owns 'src/**' and must not touch 'src/shared/**', which match the same paths: an item's forbidden paths are kept by the paths it owns",
            ],
            await CheckAsync(harness, repository, greedy, Item("one")));

        var lists = Item("lists", reuses: "missing/file.cs");
        lists["replaces"] = new JsonArray(new JsonObject { ["path"] = "../outside", ["what"] = "x" }, "x");
        lists["creates"] = new JsonArray(new JsonObject { ["what"] = "a cache", ["why"] = "" });
        lists["ownedPaths"] = new JsonArray("/", "src/lists/**");
        var empty = Item("empty", "src/empty/**", reuses: null);
        empty["ownedPaths"] = new JsonArray();
        var problems = await CheckAsync(harness, repository, lists, empty);

        Assert.Equal(
            [
                "work item 'lists' owns '/', which is empty",
                "work item 'empty' owns no path, and an item writes only the paths it owns: name them",
                "work item 'lists' reuses 'missing/file.cs', which is not in the main tree: a plan builds on what is there",
                problems[3],
                "work item 'lists''s replaces entry 2 names no path",
                "work item 'lists' creates 'a cache' and does not say why nothing there serves or where it looked: what is made from nothing says why, and where it searched first",
                "work item 'empty' names nothing it reuses, creates or replaces: say what it builds on, or what it adds where nothing there serves",
            ],
            problems);
        Assert.Contains("../outside", problems[3], StringComparison.Ordinal);
    }

    /// <summary>The path budget of the worktree an item would get counts the deepest file it owns: one past the limit is a problem of the plan.</summary>
    [Fact]
    public async Task AnItemWhoseFilesPassThePathBudget_IsAProblem()
    {
        var (harness, repository) = await RepositoryAsync(new HarnessConfig { Worktrees = new WorktreeSettings { PathLimit = 400 } });
        var deep = string.Join('/', Enumerable.Repeat("deeper", 50));

        Assert.Empty(await CheckAsync(harness, repository, Item("shallow", "src/a.cs")));

        var problem = Assert.Single(await CheckAsync(harness, repository, Item("deep", $"src/{deep}/**")));
        Assert.StartsWith("work item 'deep' would work in paths too long for this machine - its worktree, with the copies its legs keep inside it and the deepest file it owns: ", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The path budget of an item's worktree counts the copies its run's leg steps keep inside it, each holding the
    /// item's deepest file as the tree does: a file the tree has room for, and a copy has room for with its build, can
    /// still pass the limit inside the copy.
    /// </summary>
    [Fact]
    public async Task AnItemsPathBudget_CountsItsFileInsideEachCopy()
    {
        var platform = new HostPlatform();
        var (harness, repository) = await RepositoryAsync(new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", LocalCopy = true } },
            // A limit of its own, so every system measures the path: one with no limit of its own reckons none.
            Worktrees = new WorktreeSettings { PathBudgetReserve = 100, PathLimit = 100_000 },
        });
        var built = Parse("""
            name: flow
            steps:
              - name: build
                type: build
            """);
        var deep = string.Join('/', Enumerable.Repeat("deeper", 30));
        var context = await harness.ContextLoader.LoadAsync(repository.Path, Token);
        var written = $"src/{deep}".Length;
        var inCopy = FlowRunPaths.AtStart(harness.PathBudget, context, "r1", built, platform.PlatformKey, written).Result.RequiredLength;
        var limited = context with { Config = new HarnessConfig { BuildConfigs = context.Config.BuildConfigs, Legs = context.Config.Legs, Worktrees = new WorktreeSettings { PathLimit = inCopy, PathBudgetReserve = 100 } } };

        Assert.True(FlowRunPaths.AtStart(harness.PathBudget, limited, "r1", built, platform.PlatformKey).Result.IsWithinBudget, "the copy and its build alone fit");
        var tree = await harness.StartTreeAsync(repository.Path, Token);
        Assert.Empty(Validator(harness).Check([Item("deep", $"src/{deep}/**")], limited, "r1", Flow, tree));
        Assert.Empty(Validator(harness).Check([Item("shallow", "src/a/**")], limited, "r1", built, tree));

        var problem = Assert.Single(Validator(harness).Check([Item("deep", $"src/{deep}/**")], limited, "r1", built, tree));
        Assert.StartsWith("work item 'deep' would work in paths too long for this machine - ", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The plan check answers ok, the problems and the items, never a failure; given the pieces of an update it checks
    /// them with the items, each breach a problem of the plan in the output - and where it opens a round, its output
    /// items is its own and what updates owe it, as one list.
    /// </summary>
    [Fact]
    public async Task ThePlanCheck_AnswersAnOutput_AndChecksThePiecesOfAnUpdate()
    {
        var (harness, repository) = await RepositoryAsync();
        var runner = new PlanCheckRunner(harness.ContextLoader, harness.Platform, Validator(harness));
        var step = new WorkflowStep { Name = "check", Type = "validate-plan", Items = "plan.items" };
        var items = FlowValue.Of(new JsonArray(Item("one"), Item("one")));

        var checkedPlan = await runner.RunAsync(Run(step, repository.Path, new Dictionary<string, FlowValue> { ["items"] = items, ["scope"] = FlowValue.Of(new JsonArray()) }), Token);

        Assert.True(checkedPlan.Passed);
        Assert.False((bool)checkedPlan.Output!["ok"]!);
        Assert.Equal(2, checkedPlan.Output["problems"]!.AsArray().Count);
        Assert.Equal(items.ToNode()!.ToJsonString(), checkedPlan.Output["items"]!.ToJsonString());

        var updated = new JsonArray(Item("one"), Item("two", "src/two/**"));
        var pieces = FlowValue.Of(new JsonArray(Piece("added", Item("two", "src/two/**"))));

        var noUpdate = await runner.RunAsync(Run(step, repository.Path, new Dictionary<string, FlowValue> { ["items"] = items, ["scope"] = FlowValue.Missing }), Token);
        Assert.True(noUpdate.Passed, noUpdate.Error);

        var anUpdate = await runner.RunAsync(Run(step, repository.Path, new Dictionary<string, FlowValue>
        {
            ["items"] = FlowValue.Of(updated),
            ["scope"] = pieces,
            ["known"] = FlowValue.Of(new JsonArray("one")),
        }), Token);

        Assert.True(anUpdate.Passed, anUpdate.Error);
        Assert.True((bool)anUpdate.Output!["ok"]!, string.Join("; ", anUpdate.Output["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())));

        var notPieces = await runner.RunAsync(Run(step, repository.Path, new Dictionary<string, FlowValue> { ["items"] = items, ["scope"] = FlowValue.Of("added") }), Token);
        Assert.Equal(HarnessExit.ConfigInvalid, notPieces.Exit);
        Assert.Equal("'check' is given the pieces of a plan update its scope yields, and it yields no list", notPieces.Error);

        var notAList = await runner.RunAsync(Run(step, repository.Path, new Dictionary<string, FlowValue> { ["items"] = FlowValue.Of("one") }), Token);
        Assert.Equal(HarnessExit.ConfigInvalid, notAList.Exit);
        Assert.Equal("'check' checks the work items its items yields, and it yields no list", notAList.Error);
    }

    /// <summary>
    /// What the plan check finds of a reason step's answer at the answer itself is what its own run finds of the same read,
    /// by the one check both make: two items of one name, an item that must not touch what it owns, an issue owed the next
    /// round that no item settles, a piece that is no item of the plan - each problem word for word. An answer that gives no
    /// item, and one whose items are no list, hold nothing at the answer: whether no item is a problem, and what a list that
    /// is none is, are the check's own moment's to say.
    /// </summary>
    [Fact]
    public async Task AnAnswerReadAhead_IsHeldToTheOneCheckThePlanCheckMakes()
    {
        var (harness, repository) = await RepositoryAsync();
        var runner = new PlanCheckRunner(harness.ContextLoader, harness.Platform, Validator(harness));
        var step = new WorkflowStep { Name = "check", Type = "validate-plan", Items = "plan.items" };
        var forbidding = Item("two", "src/two/**");
        forbidding["forbiddenPaths"] = new JsonArray("src/two/*.cs");

        foreach (var read in new[]
        {
            new Dictionary<string, FlowValue> { ["items"] = FlowValue.Of(new JsonArray(Item("one"), Item("one"))) },
            new Dictionary<string, FlowValue> { ["items"] = FlowValue.Of(new JsonArray(Item("one"), forbidding)) },
            new Dictionary<string, FlowValue>
            {
                ["items"] = FlowValue.Of(new JsonArray(Item("one"))),
                ["settleable"] = FlowValue.Of(new JsonArray("nx-1")),
                ["next"] = FlowValue.Of(new JsonArray(new JsonObject { ["id"] = "nx-1", ["title"] = "the parser drops a field" })),
            },
            new Dictionary<string, FlowValue>
            {
                ["items"] = FlowValue.Of(new JsonArray(Item("one"))),
                ["scope"] = FlowValue.Of(new JsonArray(Piece("added", Item("two", "src/two/**")))),
                ["known"] = FlowValue.Of(new JsonArray("one")),
            },
        })
        {
            var ran = await runner.RunAsync(Run(step, repository.Path, read), Token);
            List<string> found = [.. ran.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())];

            Assert.NotEmpty(found);
            Assert.Equal(found, await runner.AnswerProblemsAsync(Run(step, repository.Path, read), Token));
        }

        var none = new Dictionary<string, FlowValue> { ["items"] = FlowValue.Of(new JsonArray()) };

        Assert.Equal([PlanValidator.NoItems], (await runner.RunAsync(Run(step, repository.Path, none), Token)).Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>()));
        Assert.Empty(await runner.AnswerProblemsAsync(Run(step, repository.Path, none), Token));
        Assert.Empty(await runner.AnswerProblemsAsync(Run(step, repository.Path, new Dictionary<string, FlowValue> { ["items"] = FlowValue.Of("one") }), Token));

        // The architecture names the owners of the judgement at the answer: the types that judge one, what the engine
        // builds of each, and the one judgement each check's run makes.
        var architecture = Prose.Document("docs", "architecture.md");

        foreach (var owner in new[]
        {
            "FlowCoordinator.AnswerChecksOf",
            "FlowStepRunners.CorrectionsOf",
            $"{nameof(WorkflowStepTypes)}.{nameof(WorkflowStepTypes.StepType)}.{nameof(WorkflowStepTypes.StepType.JudgesAnswer)}",
            $"{nameof(FlowAnswerCheck)}.{nameof(FlowAnswerCheck.ReadOf)}",
            $"{nameof(PlanCheckRunner)}.{nameof(PlanCheckRunner.AnswerProblemsAsync)}",
            $"{nameof(ClassClosureStepRunner)}.{nameof(ClassClosureStepRunner.AnswerProblemsAsync)}",
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The plan check that opens a round holds its items to the issues the run owes its next round: each named in an
    /// item's <c>settles</c>, none left unsettled, an item may name only an issue the run owes, and the items that settle
    /// them come first in its output - what the run took from its own rounds is built before the work planned around it.
    /// </summary>
    [Fact]
    public async Task ThePlanCheck_HoldsItsItemsToTheRunsNextRound_AndOrdersTheSettlingOnesFirst()
    {
        var (harness, repository) = await RepositoryAsync();
        var runner = new PlanCheckRunner(harness.ContextLoader, harness.Platform, Validator(harness));
        var step = new WorkflowStep { Name = "check", Type = "validate-plan", Items = "plan.items" };

        // The round settles the issue with its own item, planned after the work around it: the check answers ok, and
        // its items carry the settling one first.
        var settling = Item("settle-the-parser", "src/Parser.cs");
        settling["settles"] = "nx-1";
        var items = FlowValue.Of(new JsonArray(Item("around"), settling));

        var ordered = await runner.RunAsync(
            Run(step, repository.Path, new Dictionary<string, FlowValue>
            {
                ["items"] = items,
                ["settleable"] = FlowValue.Of(new JsonArray("nx-1")),
                ["next"] = FlowValue.Of(new JsonArray(NextIssue("nx-1"))),
            }),
            Token);

        Assert.True(ordered.Passed, ordered.Error);
        Assert.True((bool)ordered.Output!["ok"]!, string.Join("; ", ordered.Output["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())));
        Assert.Equal(["settle-the-parser", "around"], [.. ordered.Output["items"]!.AsArray().Select(item => item!["name"]!.GetValue<string>())]);

        // An item that names an issue the run does not owe, and an owed issue no item settles, are each a problem.
        var stranger = Item("settle-what-it-owes-not", "src/Parser.cs");
        stranger["settles"] = "nx-9";

        var unsettled = await runner.RunAsync(
            Run(step, repository.Path, new Dictionary<string, FlowValue>
            {
                ["items"] = FlowValue.Of(new JsonArray(Item("around"), stranger)),
                ["settleable"] = FlowValue.Of(new JsonArray("nx-1")),
                ["next"] = FlowValue.Of(new JsonArray(NextIssue("nx-1"))),
            }),
            Token);

        Assert.True(unsettled.Passed, unsettled.Error);
        Assert.False((bool)unsettled.Output!["ok"]!);
        Assert.Equal(
            [
                "the item 'settle-what-it-owes-not' settles 'nx-9', which is not an issue the run owes its next round",
                "the issue 'nx-1' (a defect in the parser) is owed the next round, and no work item settles it",
            ],
            [.. unsettled.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);
    }

    /// <summary>
    /// The plan check holds an item's <c>needs</c> to the issues the recovery takes whose ids the engine hands it
    /// (<c>fixes</c>): an item that needs one of them passes, its items kept as they were; one that names an issue the
    /// recovery takes no fix of is refused, for the engine would hold it for no fix - where the engine hands no issue as
    /// well; and a check handed none at all - a recovery's - refuses every need with the true reason, that its items are
    /// never held for a fix, never that the issue is not one the run is fixing.
    /// </summary>
    [Fact]
    public async Task ThePlanCheck_HoldsAnItemsNeedsToTheIssuesTheRecoveryFixes()
    {
        var (harness, repository) = await RepositoryAsync();
        var runner = new PlanCheckRunner(harness.ContextLoader, harness.Platform, Validator(harness));
        var step = new WorkflowStep { Name = "check", Type = "validate-plan", Items = "plan.items" };
        var needing = Item("build-on-the-fix", "src/Parser.cs");
        needing["needs"] = new JsonArray("triage+2");
        var stranger = Item("build-on-what-is-not-fixed", "src/Lexer.cs");
        stranger["needs"] = new JsonArray("triage+2", "pe-1");

        async Task<IReadOnlyList<string>> ProblemsAsync(JsonArray items, JsonArray? fixes)
        {
            var read = new Dictionary<string, FlowValue> { ["items"] = FlowValue.Of(items) };

            if (fixes is not null)
            {
                read["fixes"] = FlowValue.Of(fixes);
            }

            var result = await runner.RunAsync(Run(step, repository.Path, read), Token);

            Assert.True(result.Passed, result.Error);
            Assert.Equal(items.Select(FlowWorkItems.NameOf), result.Output!["items"]!.AsArray().Select(FlowWorkItems.NameOf));
            return [.. result.Output["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())];
        }

        Assert.Empty(await ProblemsAsync(new JsonArray(Item("around"), needing.DeepClone()), new JsonArray("triage", "triage+2")));
        Assert.Equal(
            ["the item 'build-on-what-is-not-fixed' needs the fix of 'pe-1', which is not an issue the run is fixing or has fixed, nor one joined to an issue it is still triaging"],
            await ProblemsAsync(new JsonArray(needing.DeepClone(), stranger.DeepClone()), new JsonArray("triage+2")));
        Assert.Equal(
            ["the item 'build-on-the-fix' needs the fix of 'triage+2', which is not an issue the run is fixing or has fixed, nor one joined to an issue it is still triaging"],
            await ProblemsAsync(new JsonArray(needing.DeepClone()), []));
        Assert.Equal(
            ["the item 'build-on-the-fix' needs the fix of 'triage+2', and the items this check reads need no fix: only an item a round opens with is held for one, and a recovery's remedy never is"],
            await ProblemsAsync(new JsonArray(needing.DeepClone()), null));
    }

    /// <summary>
    /// A check handed the issues the run owes its next round and the round a plan update's pieces join holds the list that
    /// round opens with to them - the round's own items, less the one a changed piece names, with the pieces' items, or
    /// the round's own items alone where the update answers no piece - and not the plan's own list: an issue an item of
    /// the round settles is settled though no item of the plan names it, and one only the plan's list settles is not, for
    /// the round would open without it, whether the update gives a piece or none.
    /// </summary>
    [Fact]
    public async Task ACheckHandedTheRoundAnUpdateJoins_HoldsThatRoundsListToTheOwedIssues()
    {
        var (harness, repository) = await RepositoryAsync();
        var runner = new PlanCheckRunner(harness.ContextLoader, harness.Platform, Validator(harness));
        var step = new WorkflowStep { Name = "check", Type = "validate-plan", Items = "plan.items", Scope = "plan.scope" };
        var settling = Item("settle-the-parser", "src/Parser.cs");
        settling["settles"] = "nx-1";
        var moved = Item("two", "src/moved/**");

        async Task<IReadOnlyList<string>> ProblemsAsync(JsonArray items, JsonArray round, bool pieces = true)
        {
            var result = await runner.RunAsync(
                Run(step, repository.Path, new Dictionary<string, FlowValue>
                {
                    ["items"] = FlowValue.Of(items),
                    ["scope"] = FlowValue.Of(pieces ? new JsonArray(Piece("changed", (JsonObject)moved.DeepClone())) : new JsonArray()),
                    ["round"] = FlowValue.Of(round),
                    ["known"] = FlowValue.Of(new JsonArray("one", "two", "settle-the-parser")),
                    ["settleable"] = FlowValue.Of(new JsonArray("nx-1")),
                    ["next"] = FlowValue.Of(new JsonArray(NextIssue("nx-1"))),
                }),
                Token);

            Assert.True(result.Passed, result.Error);
            return [.. result.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())];
        }

        // The round's own item settles the issue: the list it opens with settles it, though the plan's names none.
        Assert.Empty(await ProblemsAsync(new JsonArray(Item("one"), moved.DeepClone()), new JsonArray(settling.DeepClone(), Item("two", "src/two/**"))));

        // Only the plan's list settles it: the round would open without it.
        Assert.Equal(
            ["the issue 'nx-1' (a defect in the parser) is owed the next round, and no work item settles it"],
            await ProblemsAsync(new JsonArray(settling.DeepClone(), moved.DeepClone()), new JsonArray(Item("two", "src/two/**"))));

        // An update that answers no piece leaves the round's own list as it opens: the round's item settles the issue, and
        // an item of the plan alone does not.
        Assert.Empty(await ProblemsAsync(new JsonArray(Item("one")), new JsonArray(settling.DeepClone(), Item("two", "src/two/**")), pieces: false));
        Assert.Equal(
            ["the issue 'nx-1' (a defect in the parser) is owed the next round, and no work item settles it"],
            await ProblemsAsync(new JsonArray(settling.DeepClone(), Item("one")), new JsonArray(Item("two", "src/two/**")), pieces: false));
    }

    /// <summary>
    /// A plan check handed the ids of the issues the run owes and not the issues to hold its items to - the plan's, once
    /// the round its plan opens is no longer the next - takes an item that settles one, refuses one that names an issue
    /// the run does not owe, and demands nothing: an owed issue no item of it settles is no problem of its, and its items
    /// keep their order, for the round's own check holds the round to them. A check handed an empty list refuses every
    /// settles as naming no owed issue; one handed neither - a recovery's - refuses every settles with the true reason,
    /// that its items settle no issue.
    /// </summary>
    [Fact]
    public async Task APlanCheckHandedTheOwedIds_TakesAnItemThatSettlesOne_AndDemandsNothing()
    {
        var (harness, repository) = await RepositoryAsync();
        var runner = new PlanCheckRunner(harness.ContextLoader, harness.Platform, Validator(harness));
        var step = new WorkflowStep { Name = "check", Type = "validate-plan", Items = "plan.items" };
        var settling = Item("settle-the-parser", "src/Parser.cs");
        settling["settles"] = "nx-1";

        var taken = await runner.RunAsync(
            Run(step, repository.Path, new Dictionary<string, FlowValue>
            {
                ["items"] = FlowValue.Of(new JsonArray(Item("around"), settling)),
                ["settleable"] = FlowValue.Of(new JsonArray("nx-1", "nx-2")),
            }),
            Token);

        Assert.True(taken.Passed, taken.Error);
        Assert.True((bool)taken.Output!["ok"]!, string.Join("; ", taken.Output["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())));
        Assert.Equal(["around", "settle-the-parser"], [.. taken.Output["items"]!.AsArray().Select(item => item!["name"]!.GetValue<string>())]);

        var stranger = Item("settle-what-it-owes-not", "src/Parser.cs");
        stranger["settles"] = "nx-9";

        var refused = await runner.RunAsync(
            Run(step, repository.Path, new Dictionary<string, FlowValue>
            {
                ["items"] = FlowValue.Of(new JsonArray(Item("around"), stranger)),
                ["settleable"] = FlowValue.Of(new JsonArray("nx-1")),
            }),
            Token);

        Assert.True(refused.Passed, refused.Error);
        Assert.False((bool)refused.Output!["ok"]!);
        Assert.Equal(
            ["the item 'settle-what-it-owes-not' settles 'nx-9', which is not an issue the run owes its next round"],
            [.. refused.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);

        var owingNone = await runner.RunAsync(
            Run(step, repository.Path, new Dictionary<string, FlowValue> { ["items"] = FlowValue.Of(new JsonArray(Item("around"), settling.DeepClone())), ["settleable"] = FlowValue.Of(new JsonArray()) }),
            Token);

        Assert.True(owingNone.Passed, owingNone.Error);
        Assert.False((bool)owingNone.Output!["ok"]!);
        Assert.Equal(
            ["the item 'settle-the-parser' settles 'nx-1', which is not an issue the run owes its next round"],
            [.. owingNone.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);

        // A check handed no ids at all - a recovery's - says the true reason: its items settle no issue.
        var unowed = await runner.RunAsync(
            Run(step, repository.Path, new Dictionary<string, FlowValue> { ["items"] = FlowValue.Of(new JsonArray(Item("around"), settling.DeepClone())) }),
            Token);

        Assert.True(unowed.Passed, unowed.Error);
        Assert.False((bool)unowed.Output!["ok"]!);
        Assert.Equal(
            ["the item 'settle-the-parser' settles 'nx-1', and the items this check reads settle no issue: only an item a round opens with settles one the run owes its next round, and a recovery's remedy never does"],
            [.. unowed.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);
    }

    /// <summary>One issue the run owes its next round, as the engine hands it to the check that opens a round.</summary>
    private static JsonObject NextIssue(string id) => new()
    {
        ["id"] = id,
        ["title"] = "a defect in the parser",
        ["description"] = "what it is",
        ["where"] = new JsonArray("src/Parser.cs"),
        ["files"] = new JsonArray("src/Parser.cs"),
        ["build"] = "the leg the parser tests build",
        ["measureFirst"] = new JsonArray(),
    };

    /// <summary>One piece of a plan update, as the plan's scope concluded it.</summary>
    private static JsonObject Piece(string kind, JsonObject? item = null, string? forId = null, string changed = "what the update changes")
        => new()
        {
            ["kind"] = kind,
            ["item"] = item,
            ["for"] = forId,
            ["changed"] = changed,
            ["keep"] = "what stays",
            ["undo"] = "what is taken back",
            ["instead"] = "what is done instead",
        };

    /// <summary>
    /// Each breach of the pieces, worded once - by the one rule of a piece's shape the engine reads a planning step's
    /// conclusion by, so a piece the check passes is one the engine takes: the wrong shape, the wrong kind, no change
    /// said (the first breach of a piece is its problem), an item the plan does not hold, a piece that names what the
    /// plan never had, a piece named twice, a told piece that names nothing.
    /// </summary>
    [Fact]
    public async Task EachBreachOfThePieces_IsAProblem()
    {
        var (harness, repository) = await RepositoryAsync();
        var runner = new PlanCheckRunner(harness.ContextLoader, harness.Platform, Validator(harness));
        var step = new WorkflowStep { Name = "check", Type = "validate-plan", Items = "plan.items" };
        var items = new JsonArray(Item("one"));

        async Task<IReadOnlyList<string>> ProblemsAsync(Dictionary<string, FlowValue> read)
        {
            var result = await runner.RunAsync(Run(step, repository.Path, read), Token);
            Assert.True(result.Passed, result.Error);
            return [.. result.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())];
        }

        Assert.Equal(
            [
                "piece 1 of the plan's scope is of the wrong shape: it is no object with a kind, and a piece is one of 'added', 'changed', 'removed' or 'told'",
                "piece 2 of the plan's scope is of the kind 'redone', and a piece is one of 'added', 'changed', 'removed' or 'told'",
                "piece 3 of the plan's scope says not what the update changes for its work, and a piece always does",
                "piece 4 of the plan's scope is told, and names no work under way: a told piece tells work that is going on",
            ],
            await ProblemsAsync(new Dictionary<string, FlowValue>
            {
                ["items"] = FlowValue.Of(items),
                ["scope"] = FlowValue.Of(new JsonArray("added", new JsonObject { ["kind"] = "redone" }, new JsonObject { ["kind"] = "added", ["changed"] = " ", ["item"] = Item("two", "src/two/**") }, new JsonObject { ["kind"] = "told", ["changed"] = "it" })),
                ["known"] = FlowValue.Of(new JsonArray()),
            }));

        Assert.Equal(
            [
                "piece 1 of the plan's scope holds a work item with no name, and a work item is known by its name",
                "the piece for 'two' is not an item of the plan's list, by name and member for member: an item the update adds or changes is an item of the updated plan",
            ],
            await ProblemsAsync(new Dictionary<string, FlowValue>
            {
                ["items"] = FlowValue.Of(items),
                ["scope"] = FlowValue.Of(new JsonArray(new JsonObject { ["kind"] = "changed", ["changed"] = "it", ["item"] = new JsonObject { ["title"] = "no name" } }, Piece("added", Item("two", "src/two/**")))),
                ["known"] = FlowValue.Of(new JsonArray()),
            }));

        Assert.Equal(
            [
                "the pieces of the plan's scope name the work item 'two' twice, and a work item is named by one piece of an update",
                "the piece for 'two' is 'removed', and no work item the plan had is named so: one an instance of the work-item group carries, or an item of the list this check checked on its visit before",
                "the piece for 'one' is 'removed', and no work item the plan had is named so: one an instance of the work-item group carries, or an item of the list this check checked on its visit before",
                "2 work items are named 'two', and a work item is known by its name: name each once",
                "work items 'two' and 'two' both own what 'src/two/**' and 'src/two/**' match: no file is written by two items, so give it to one",
            ],
            await ProblemsAsync(new Dictionary<string, FlowValue>
            {
                ["items"] = FlowValue.Of(new JsonArray(Item("one"), Item("two", "src/two/**"))),
                ["scope"] = FlowValue.Of(new JsonArray(Piece("added", Item("two", "src/two/**")), Piece("removed", Item("two", "src/two/**")), Piece("removed", Item("one")))),
                ["known"] = FlowValue.Of(new JsonArray("other")),
            }));
    }

    /// <summary>
    /// The items an update places are checked with those of the round it joins, as one list: an item that owns a file
    /// an item of the round owns, built or not, is a problem of the updated plan - and where the run has no round yet,
    /// the pieces are checked among themselves.
    /// </summary>
    [Fact]
    public async Task AnItemAnUpdatePlaces_IsCheckedWithTheItemsOfTheRoundItJoins()
    {
        var (harness, repository) = await RepositoryAsync();
        var runner = new PlanCheckRunner(harness.ContextLoader, harness.Platform, Validator(harness));
        var step = new WorkflowStep { Name = "check", Type = "validate-plan", Items = "plan.items" };

        async Task<IReadOnlyList<string>> ProblemsAsync(Dictionary<string, FlowValue> read)
        {
            var result = await runner.RunAsync(Run(step, repository.Path, read), Token);
            Assert.True(result.Passed, result.Error);
            return [.. result.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())];
        }

        var updated = new JsonArray(Item("one"), Item("two", "src/two/**"));

        Assert.Empty(await ProblemsAsync(new Dictionary<string, FlowValue>
        {
            ["items"] = FlowValue.Of(updated),
            ["scope"] = FlowValue.Of(new JsonArray(Piece("added", Item("two", "src/two/**")))),
            ["round"] = FlowValue.Of(new JsonArray(Item("one"))),
            ["known"] = FlowValue.Of(new JsonArray("one")),
        }));

        // An item that owns a file an item of the round owns, built or not, is a problem of the updated plan.
        Assert.Equal(
            ["work items 'one' and 'two' both own what 'src/one/**' and 'src/one/**' match: no file is written by two items, so give it to one"],
            await ProblemsAsync(new Dictionary<string, FlowValue>
            {
                ["items"] = FlowValue.Of(new JsonArray(Item("two", "src/one/**"))),
                ["scope"] = FlowValue.Of(new JsonArray(Piece("added", Item("two", "src/one/**")))),
                ["round"] = FlowValue.Of(new JsonArray(Item("one"))),
                ["known"] = FlowValue.Of(new JsonArray("one")),
            }));

        // The work item a changed or a removed piece names is taken out of the round's list: its item replaces it.
        Assert.Empty(await ProblemsAsync(new Dictionary<string, FlowValue>
        {
            ["items"] = FlowValue.Of(new JsonArray(Item("one", "src/one/**"))),
            ["scope"] = FlowValue.Of(new JsonArray(Piece("changed", Item("one", "src/one/**")))),
            ["round"] = FlowValue.Of(new JsonArray(Item("one"))),
            ["known"] = FlowValue.Of(new JsonArray("one")),
        }));

        // Where the run has no round yet, the pieces are checked among themselves.
        Assert.Equal(
            [
                "the pieces of the plan's scope name the work item 'one' twice, and a work item is named by one piece of an update",
                "2 work items are named 'one', and a work item is known by its name: name each once",
                "work items 'one' and 'one' both own what 'src/one/**' and 'src/one/**' match: no file is written by two items, so give it to one",
            ],
            await ProblemsAsync(new Dictionary<string, FlowValue>
            {
                ["items"] = FlowValue.Of(new JsonArray(Item("one", "src/one/**"), Item("two", "src/two/**"))),
                ["scope"] = FlowValue.Of(new JsonArray(Piece("added", Item("one", "src/one/**")), Piece("added", Item("one", "src/one/**")))),
                ["known"] = FlowValue.Of(new JsonArray("one")),
            }));
    }

    /// <summary>
    /// The check that opens a round checks one list - its own items and every work item plan updates owe it - and its
    /// output items is that list, so the group expands from what was checked; no count of items is refused, of a round's
    /// own list or of what updates place in it.
    /// </summary>
    [Fact]
    public async Task TheCheckThatOpensARound_ChecksItsOwnItemsAndTheOwedOnesAsOneList()
    {
        var (harness, repository) = await RepositoryAsync();
        var runner = new PlanCheckRunner(harness.ContextLoader, harness.Platform, Validator(harness));
        var step = new WorkflowStep { Name = "item-check", Type = "validate-plan", Items = "plan.items" };
        var own = new JsonArray([.. Enumerable.Range(1, 8).Select(at => (JsonNode?)Item($"item-{at}", $"src/{at}/**"))]);

        var opened = await runner.RunAsync(Run(step, repository.Path, new Dictionary<string, FlowValue>
        {
            ["items"] = FlowValue.Of(own),
            ["owed"] = FlowValue.Of(new JsonArray(Item("owed", "src/owed/**"))),
        }), Token);

        Assert.True(opened.Passed, opened.Error);
        Assert.True((bool)opened.Output!["ok"]!, string.Join("; ", opened.Output["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())));
        Assert.Equal(9, opened.Output["items"]!.AsArray().Count);
        Assert.Equal("owed", opened.Output["items"]!.AsArray()[8]!["name"]!.GetValue<string>());

        var colliding = await runner.RunAsync(Run(step, repository.Path, new Dictionary<string, FlowValue>
        {
            ["items"] = FlowValue.Of(new JsonArray(Item("one"))),
            ["owed"] = FlowValue.Of(new JsonArray(Item("owed", "src/one/**"))),
        }), Token);

        Assert.True(colliding.Passed);
        Assert.False((bool)colliding.Output!["ok"]!);
        Assert.Equal(
            ["work items 'one' and 'owed' both own what 'src/one/**' and 'src/one/**' match: no file is written by two items, so give it to one"],
            [.. colliding.Output["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())]);

        // A round's own list holds as many items as its plan gave: thirty, and none owed, open a round with no problem.
        var thirty = await runner.RunAsync(Run(step, repository.Path, new Dictionary<string, FlowValue>
        {
            ["items"] = FlowValue.Of(new JsonArray([.. Enumerable.Range(1, 30).Select(at => (JsonNode?)Item($"item-{at}", $"src/{at}/**"))])),
            ["owed"] = FlowValue.Of(new JsonArray()),
        }), Token);

        Assert.Equal(
            (true, 30),
            ((bool)thirty.Output!["ok"]!, thirty.Output["items"]!.AsArray().Count));
    }

    /// <summary>
    /// A work item is known by its name across the run - its record of affected tests keeps an item's units under it, and
    /// a plan update finds the item by it - so the check that opens a round refuses a round's own item under a name an
    /// earlier round's item carries, and passes an owed one under it, for a changed or a removed piece names the item it
    /// does again or takes back; a round that carries the owed work alone - one added to a work-item group with no loop
    /// around it - builds none of its own items, which the round before built under those names, so none is refused;
    /// and a plan update's added piece under such a name is refused, its changed one passed.
    /// </summary>
    [Fact]
    public async Task ANameAnEarlierRoundCarries_IsRefusedToARoundsOwnItemAndToAnAddedPiece_AndPassedToAChangedOne()
    {
        var (harness, repository) = await RepositoryAsync();
        var runner = new PlanCheckRunner(harness.ContextLoader, harness.Platform, Validator(harness));
        var opening = new WorkflowStep { Name = "item-check", Type = "validate-plan", Items = "plan.items" };
        const string Reused = "work item 'one' is named as a work item an earlier round of the run carries, and a work item is known by its name across the run - its tests and its updates are kept under it: a further item is named for what it settles, and one that does that item again is a changed piece of it";

        async Task<IReadOnlyList<string>> ProblemsAsync(WorkflowStep step, Dictionary<string, FlowValue> read)
        {
            var result = await runner.RunAsync(Run(step, repository.Path, read), Token);
            Assert.True(result.Passed, result.Error);
            return [.. result.Output!["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())];
        }

        Assert.Equal(
            [Reused],
            await ProblemsAsync(opening, new Dictionary<string, FlowValue>
            {
                ["items"] = FlowValue.Of(new JsonArray(Item("one"), Item("two", "src/two/**"))),
                ["owed"] = FlowValue.Of(new JsonArray()),
                ["carried"] = FlowValue.Of(new JsonArray("one", "three")),
            }));

        // An owed item under a carried name replaces the round's own one of it, and is the item a piece does again.
        Assert.Empty(await ProblemsAsync(opening, new Dictionary<string, FlowValue>
        {
            ["items"] = FlowValue.Of(new JsonArray(Item("one"), Item("two", "src/two/**"))),
            ["owed"] = FlowValue.Of(new JsonArray(Item("one"))),
            ["carried"] = FlowValue.Of(new JsonArray("one", "three")),
        }));

        // The round's own items, which a round carrying the owed work alone does not build, are held to no name; the
        // same list opening a round that builds them is refused each.
        Dictionary<string, FlowValue> Added(bool owedAlone) => new()
        {
            ["items"] = FlowValue.Of(new JsonArray(Item("one"), Item("two", "src/two/**"))),
            ["owed"] = FlowValue.Of(new JsonArray(Item("three", "src/three/**"))),
            ["owedAlone"] = FlowValue.Of(owedAlone),
            ["carried"] = FlowValue.Of(new JsonArray("one", "two")),
        };

        Assert.Empty(await ProblemsAsync(opening, Added(owedAlone: true)));
        Assert.Equal([Reused, Reused.Replace("'one'", "'two'", StringComparison.Ordinal)], await ProblemsAsync(opening, Added(owedAlone: false)));

        // A check that opens no round - its items are the plan's, which holds the items rounds built - refuses none.
        var check = new WorkflowStep { Name = "check", Type = "validate-plan", Items = "plan.items" };

        Assert.Empty(await ProblemsAsync(check, new Dictionary<string, FlowValue>
        {
            ["items"] = FlowValue.Of(new JsonArray(Item("one"), Item("two", "src/two/**"))),
            ["carried"] = FlowValue.Of(new JsonArray("one")),
        }));

        Assert.Equal(
            ["the piece for 'one' is 'added', and a work item a round of the run carries is named so, and a work item is known by its name across the run: name the new item for what it settles, or make the piece 'changed' to do that item again"],
            await ProblemsAsync(check, new Dictionary<string, FlowValue>
            {
                ["items"] = FlowValue.Of(new JsonArray(Item("one"))),
                ["scope"] = FlowValue.Of(new JsonArray(Piece("added", Item("one")))),
                ["known"] = FlowValue.Of(new JsonArray()),
                ["carried"] = FlowValue.Of(new JsonArray("one")),
            }));
        Assert.Empty(await ProblemsAsync(check, new Dictionary<string, FlowValue>
        {
            ["items"] = FlowValue.Of(new JsonArray(Item("one"))),
            ["scope"] = FlowValue.Of(new JsonArray(Piece("changed", Item("one")))),
            ["known"] = FlowValue.Of(new JsonArray("one")),
            ["carried"] = FlowValue.Of(new JsonArray("one")),
        }));
    }

    private static readonly WorkflowStep CreateStep = new() { Name = "make", Type = "create-agent", Agent = "worker.md" };

    /// <summary>A run's orchestrator, as the run's start makes it.</summary>
    private static async Task<OrchestratorLayout> RunAsync(HarnessFactory harness, TempDirectory repository)
    {
        var context = await harness.ContextLoader.LoadAsync(repository.Path, Token);
        var layout = OrchestratorLayout.Of(context.Layout, "r1");
        harness.FileSystem.CreateDirectory(layout.Directory);
        harness.OrchestrationStore.WriteOrchestrator(layout, new OrchestratorRecord { Kind = OrchestratorRecord.KindName, Name = "r1", Model = "flow-engine", Parallel = 2, CreatedAt = DateTimeOffset.UnixEpoch });
        return layout;
    }

    /// <summary>The agent the engine named is made, with its worktree, for its model; made again it is done, and handed nothing more.</summary>
    [Fact]
    public async Task CreateAgent_MakesTheNamedAgent_AndMadeAgainIsDone()
    {
        var (harness, repository) = await RepositoryAsync();
        var layout = await RunAsync(harness, repository);
        var run = Run(CreateStep, repository.Path, made: new FlowMadeAgent("a1", "model-one"));

        var made = await Create(harness).RunAsync(run, Token);
        var again = await Create(harness).RunAsync(run, Token);

        Assert.True(made.Passed, made.Error);
        Assert.Equal("""{"agent":"a1"}""", made.Output!.ToJsonString());
        var record = harness.OrchestrationStore.ReadAgent(layout, "a1")!;
        Assert.Equal("model-one", record.Model);
        Assert.False(record.Reads);
        Assert.NotNull(record.Base);
        Assert.True(again.Passed, again.Error);
        Assert.Equal("""{"agent":"a1"}""", again.Output!.ToJsonString());
    }

    /// <summary>
    /// A making refused because a run holds the main tree ends the step with the cause of a held lock, which the retry
    /// table waits out, rather than as a failure; once the tree is let go the agent is made.
    /// </summary>
    [Fact]
    public async Task CreateAgent_RefusedForAHeldMainTree_CarriesTheHeldLocksCause()
    {
        var (harness, repository) = await RepositoryAsync();
        var layout = await RunAsync(harness, repository);
        var run = Run(CreateStep, repository.Path, made: new FlowMadeAgent("a1", "model-one"));
        var attempt = await harness.RunLock.TryAcquireAsync(
            new HarnessLayout(repository.Path, repository.Path),
            new LockRequest { Host = "local", Tree = repository.Path, Scope = LockScope.TreeExclusive, RunId = RunId.New(), Command = "test" },
            Token);
        FlowStepResult refused;

        await using (attempt.Handle ?? throw new InvalidOperationException($"the test could not hold the main tree: {attempt.HeldBy}"))
        {
            refused = await Create(harness).RunAsync(run, Token);
        }

        Assert.Equal((false, HarnessExit.Refused, OutcomeCause.LockHeld), (refused.Passed, refused.Exit, refused.Cause));
        Assert.True(FlowRetry.RunsAgain(refused.Exit, refused.Cause));
        Assert.Null(harness.OrchestrationStore.ReadAgent(layout, "a1"));

        var made = await Create(harness).RunAsync(run, Token);

        Assert.True(made.Passed, made.Error);
    }

    /// <summary>An agent made part way, its worktree's commit recorded and never seeded, is finished by seeding it.</summary>
    [Fact]
    public async Task CreateAgent_FinishesAnAgentMadePartWay_ByItsSeeding()
    {
        var (harness, repository) = await RepositoryAsync();
        var layout = await RunAsync(harness, repository);
        var run = Run(CreateStep, repository.Path, made: new FlowMadeAgent("a1", "model-one"));
        Assert.True((await Create(harness).RunAsync(run, Token)).Passed);
        File.Delete(layout.SeedFile("a1"));
        Assert.NotNull(AgentService.MakingUnfinished(harness.OrchestrationStore.ReadAgent(layout, "a1")!, null));

        var finished = await Create(harness).RunAsync(run, Token);

        Assert.True(finished.Passed, finished.Error);
        Assert.NotNull(harness.OrchestrationStore.ReadSeed(layout, "a1"));
    }

    /// <summary>
    /// A making whose hand-over of the main tree's uncommitted state stopped part way is not whole: made again by hand it
    /// says so, never that it is already as asked, and the step run again hands it the rest by seeding it - so the step
    /// never passes with an agent missing part of what it was to be handed.
    /// </summary>
    [Fact]
    public async Task CreateAgent_WhoseHandOverStoppedPartWay_IsFinishedBySeedingTheRest()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmain edit\n");
        var stopping = kit.Harness.Agents(new AgentServiceTests.UncopyableFileSystem(kit.Harness.FileSystem, Path.Combine(kit.Main, "b.txt")), kit.Harness.AnchorRegistryService);
        var run = Run(CreateStep, kit.Main, made: new FlowMadeAgent("ag", "model-b"), run: "o1");

        var stopped = await new CreateAgentRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, stopping).RunAsync(run, Token);

        Assert.Equal((false, HarnessExit.Incomplete), (stopped.Passed, stopped.Exit));
        var part = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;
        Assert.Equal(["a.txt"], part.Paths.Keys);
        Assert.True(part.HandOverStopped);

        var typed = await kit.Harness.AgentService.CreateAsync(kit.Main, "o1", "ag", "model-b", empty: false, session: null, Token);

        Assert.Equal(HarnessExit.Refused, typed.ExitCode);
        Assert.Equal(
            "Agent 'ag' of 'o1' exists, and making it did not finish: handing it the main tree's uncommitted state stopped part way, and its seed names only "
                + "what it was handed; 'dssharness seed-agent o1 ag --force' hands it the rest.",
            typed.Message);

        var finished = await Create(kit.Harness).RunAsync(run, Token);
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.True(finished.Passed, finished.Error);
        Assert.Equal(["a.txt", "b.txt"], seed.Paths.Keys.Order(StringComparer.Ordinal));
        Assert.Null(seed.HandOverStopped);
        Assert.Null(AgentService.MakingUnfinished(kit.Record("ag"), seed));
    }

    /// <summary>An agent made part way with no commit recorded is deleted, its work abandoned, and its name spent; a deleted one's name is spent too.</summary>
    [Fact]
    public async Task CreateAgent_SpendsTheNameOfAnAgentItCannotFinish_OrThatWasDeleted()
    {
        var (harness, repository) = await RepositoryAsync();
        var layout = await RunAsync(harness, repository);
        var root = (await harness.ContextLoader.LoadAsync(repository.Path, Token)).Config.Worktrees.Root;
        harness.FileSystem.CreateDirectory(layout.AgentDirectory("a1"));
        harness.OrchestrationStore.WriteAgent(layout, new AgentRecord { Kind = AgentRecord.KindName, Name = "a1", Orchestrator = "r1", Model = "model-one", CreatedAt = DateTimeOffset.UnixEpoch, Worktree = WorktreeAddress.Nested("r1", "a1").Name, WorktreesRoot = root, State = AgentStates.Live });

        var dropped = await Create(harness).RunAsync(Run(CreateStep, repository.Path, made: new FlowMadeAgent("a1", "model-one")), Token);

        Assert.False(dropped.Passed);
        Assert.True(dropped.NameSpent, dropped.Error);
        Assert.Equal(HarnessExit.Refused, dropped.Exit);
        Assert.Equal("agent 'a1' of 'r1' was made part way and recorded no commit its worktree was made from, so it was deleted, its work abandoned; its name is spent, and the step's next attempt makes a new agent", dropped.Error);
        Assert.Equal(AgentStates.Deleted, harness.OrchestrationStore.ReadAgent(layout, "a1")!.State);

        var spent = await Create(harness).RunAsync(Run(CreateStep, repository.Path, made: new FlowMadeAgent("a1", "model-one")), Token);
        Assert.True(spent.NameSpent);
        Assert.Equal("agent 'a1' of 'r1' was deleted, and an agent's name is never given twice; the step's next attempt makes a new agent", spent.Error);
    }

    /// <summary>
    /// An agent made empty is handed nothing; one there, whole, made for another model is refused, its name kept; and one
    /// made part way whose seeding cannot be finished fails, its name kept for the next attempt.
    /// </summary>
    [Fact]
    public async Task CreateAgent_MakesItEmpty_RefusesAnotherModel_AndSaysASeedingThatFails()
    {
        var (harness, repository) = await RepositoryAsync();
        var layout = await RunAsync(harness, repository);
        var context = await harness.ContextLoader.LoadAsync(repository.Path, Token);
        var empty = new WorkflowStep { Name = "make", Type = "create-agent", Agent = "worker.md", Empty = true };

        var made = await Create(harness).RunAsync(Run(empty, repository.Path, made: new FlowMadeAgent("a1", "model-one")), Token);

        Assert.True(made.Passed, made.Error);
        Assert.True(harness.OrchestrationStore.ReadSeed(layout, "a1")!.Empty);

        // Run again over its whole agent, the empty step is done: a whole agent is never handed anything again.
        var again = await Create(harness).RunAsync(Run(empty, repository.Path, made: new FlowMadeAgent("a1", "model-one")), Token);

        Assert.True(again.Passed, again.Error);
        Assert.Equal("""{"agent":"a1"}""", again.Output!.ToJsonString());
        Assert.True(harness.OrchestrationStore.ReadSeed(layout, "a1")!.Empty);

        var other = await Create(harness).RunAsync(Run(CreateStep, repository.Path, made: new FlowMadeAgent("a1", "model-two")), Token);

        Assert.False(other.Passed);
        Assert.False(other.NameSpent);
        Assert.Equal(HarnessExit.Refused, other.Exit);
        Assert.StartsWith("Agent 'a1' of 'r1' exists, created for model 'model-one'.", other.Error, StringComparison.Ordinal);

        File.Delete(layout.SeedFile("a1"));
        Directory.Delete(WorktreeAddress.Nested("r1", "a1").PathUnder(context.Layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root)), recursive: true);

        var unseeded = await Create(harness).RunAsync(Run(CreateStep, repository.Path, made: new FlowMadeAgent("a1", "model-one")), Token);

        Assert.False(unseeded.Passed);
        Assert.False(unseeded.NameSpent);
        Assert.StartsWith("agent 'a1' of 'r1' was made part way, and finishing its seeding did not succeed: ", unseeded.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// An agent made part way with no commit recorded whose deletion stops is left closed, its name kept and the step
    /// failed with what stopped it; found closed, it is refused by a making typed by hand, and the step finishes its
    /// removal and spends the name.
    /// </summary>
    [Fact]
    public async Task CreateAgent_FindingItsAgentClosed_FinishesItsRemoval_AndSpendsTheName()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "evidence/run.log", "measured\n");
        kit.Harness.OrchestrationStore.UpdateAgent(kit.Layout, "ag", record => record with { Base = null });
        File.Delete(kit.Layout.SeedFile("ag"));
        var late = new AgentDeletionTests.LateEvidenceFileSystem(kit.Harness.FileSystem, Path.Combine(worktree, "evidence", "late.log"));
        var run = Run(CreateStep, kit.Main, made: new FlowMadeAgent("ag", "model-b"), run: "o1");

        var stopped = await new CreateAgentRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, kit.Harness.Agents(late, kit.Harness.AnchorRegistryService)).RunAsync(run, Token);

        Assert.False(stopped.Passed);
        Assert.False(stopped.NameSpent);
        Assert.Equal(HarnessExit.Incomplete, stopped.Exit);
        Assert.StartsWith("agent 'ag' of 'o1' was made part way and recorded no commit its worktree was made from, and deleting it did not finish: ", stopped.Error, StringComparison.Ordinal);
        Assert.Equal(AgentStates.Closed, kit.Record("ag").State);

        var typed = await kit.Harness.AgentService.CreateAsync(kit.Main, "o1", "ag", "model-b", empty: false, session: null, Token);

        Assert.Equal(HarnessExit.Refused, typed.ExitCode);
        Assert.Equal("Agent 'ag' of 'o1' is closed and its worktree's removal began, so it is never made again: 'dssharness delete-agent o1 ag --apply' finishes the removal, and an agent's name is never used twice. Nothing was changed.", typed.Message);

        var spent = await Create(kit.Harness).RunAsync(run, Token);

        Assert.True(spent.NameSpent, spent.Error);
        Assert.Equal(HarnessExit.Refused, spent.Exit);
        Assert.Equal("agent 'ag' of 'o1' was closed, its worktree's removal begun, so it was deleted; its name is spent, and the step's next attempt makes a new agent", spent.Error);
        Assert.Equal(AgentStates.Deleted, kit.Record("ag").State);
    }

    /// <summary>A step no model resolves for is refused, naming where one is said; one the engine named no agent for is the engine's fault.</summary>
    [Fact]
    public async Task CreateAgent_WithNoModelOrNoName_IsRefused()
    {
        var (harness, repository) = await RepositoryAsync();
        _ = await RunAsync(harness, repository);

        var unresolved = await Create(harness).RunAsync(Run(CreateStep, repository.Path, made: new FlowMadeAgent("a1", null)), Token);
        var unnamed = await Create(harness).RunAsync(Run(CreateStep, repository.Path), Token);

        Assert.Equal(HarnessExit.ConfigInvalid, unresolved.Exit);
        Assert.Equal("'make' makes agent 'a1', and no model resolves for it: it is made for the model of the first reason step that works in it, or the default tier's first; name a tier under defaults.agentTier, or a model on that step", unresolved.Error);
        Assert.Equal(HarnessExit.InternalError, unnamed.Exit);
    }

    /// <summary>
    /// The agent of a read-only reason step is made by the same making, as one that only reads: its tree a repository of
    /// its own at the main checkout's HEAD, as every agent's is.
    /// </summary>
    [Fact]
    public async Task TheMaking_MakesAnAgentThatOnlyReads_ItsTreeAtTheMainCheckoutsHead()
    {
        var (harness, repository) = await RepositoryAsync();
        var layout = await RunAsync(harness, repository);
        var head = (await harness.GitClient.ResolveCommitAsync(repository.Path, "HEAD", Token))!;

        var made = await Create(harness).MakeAsync(Run(new WorkflowStep { Name = "think", Type = "reason" }, repository.Path, made: new FlowMadeAgent("a2", "model-one")), readsOnly: true, Token);

        Assert.True(made.Passed, made.Error);

        var record = harness.OrchestrationStore.ReadAgent(layout, "a2")!;
        var tree = record.WorktreePath(new HarnessLayout(repository.Path, repository.Path))!;

        Assert.True(record.Reads);
        Assert.Equal((AgentRecord.TreeRepository, head), (record.Tree, record.Base));
        Assert.Equal(head, await harness.GitClient.ResolveCommitAsync(tree, "HEAD", Token));
    }
}
