using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.Composition;
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

namespace DssHarness.Tests;

/// <summary>
/// The machine step runners of slice 20a, each over a real repository: orient's facts, the plan check and the machine's
/// check of a plan's work items, and create-agent's making of the agent the engine named - none, whole, part made with
/// and without its worktree's commit, deleted, and with no worktree - and the dispatcher that serves each type it has a
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

        public void Used(long tokens, double usd) { }

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
        Assert.Equal(output.Select(member => member.Key).Order(StringComparer.Ordinal), new[] { "anchors", "baseBranch", "branch", "ciConfigured", "dirtyPaths", "hasLegs", "hasMutations", "hasRemote", "inFlight", "legCount", "openAgents", "startCommit", "tests" });
    }

    /// <summary>
    /// With a remote, the base branch is the pull request's base where the engine filled one, else the remote's default;
    /// CI declared is then configured; anchors, mutation arms, the open agents of every run and who holds the main tree
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
        harness.OrchestrationStore.WriteAgent(other, new AgentRecord { Kind = AgentRecord.KindName, Name = "a5", Orchestrator = "r9", Model = "m", CreatedAt = DateTimeOffset.UnixEpoch, Access = AgentRecord.AccessRead, State = AgentStates.Live });

        var held = await harness.RunLock.TryAcquireAsync(
            context.Layout,
            new LockRequest { Host = HostId.Local.ToString(), Tree = harness.RunLock.Key(HostId.Local.ToString(), repository.Path), Scope = LockScope.TreeExclusive, RunId = RunId.New(), Command = "build" },
            Token);
        await using var handle = held.Handle!;

        var head = (await harness.RunGitAsync(repository.Path, ["rev-parse", "HEAD"], Token)).StandardOutput.Trim();
        var fromRemote = await Orient(harness).RunAsync(Run(OrientStep, repository.Path), Token);
        var fromPullRequest = await Orient(harness).RunAsync(Run(OrientStep, repository.Path, new Dictionary<string, FlowValue> { ["base"] = FlowValue.Of("release") }), Token);

        Assert.True(fromRemote.Passed, fromRemote.Error);
        var output = fromRemote.Output!.AsObject();
        Assert.Equal("trunk", (string?)output["baseBranch"]);
        Assert.Equal(head, (string?)output["startCommit"]);
        Assert.True((bool)output["hasRemote"]!);
        Assert.True((bool)output["ciConfigured"]!);
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

        using var provider = new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = "test" }, OperationId.New())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.Equal(FlowStepRunners.Served, provider.GetServices<IFlowStepTypeRunner>().Select(runner => runner.Type));
        Assert.Contains(
            $"the steps `FlowStepRunners.Served` lists are served - {string.Join(", ", FlowStepRunners.Served.Select(type => $"`{type}`"))} - and a step of any other type fails until its runner lands",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);

        // The README counts them in words and names each.
        string[] words = ["Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen", "Twenty"];
        var readme = Prose.Document("README.md");
        var served = readme.IndexOf($"{words[FlowStepRunners.Served.Count]} steps are served so far:", StringComparison.Ordinal);

        Assert.True(served >= 0, "the README counts the steps served");
        var listed = readme[served..readme.IndexOf("A check that finds red", served, StringComparison.Ordinal)];
        Assert.All(FlowStepRunners.Served, type => Assert.Contains($"`{type}`", listed, StringComparison.Ordinal));
        Assert.IsType<FlowStepRunners>(provider.GetRequiredService<IFlowStepRunner>());
    }

    /// <summary>
    /// A reason step's start syncs its view through the dispatcher before its runner is asked - its agent's, the base for
    /// one that reads, none for an agent the run has no record of - where the run has a graph, each a line of the sync log
    /// naming the step's start; a run with no graph has none made.
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

        Assert.Equal([("base", "test"), ("ag", "think:start"), ("base", "think:start")], lines);
        Assert.Contains("b.txt", kit.Harness.OrchestrationStore.ReadGraphView(kit.Layout, "ag")!.Files.Keys);
        Assert.Equal(
            (await kit.Harness.RunGitAsync(kit.Main, ["hash-object", "a.txt"], Token)).StandardOutput.Trim(),
            kit.Harness.OrchestrationStore.ReadGraphManifest(kit.Layout)!.Files["a.txt"].Blob);
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
        };

    private static async Task<IReadOnlyList<string>> CheckAsync(HarnessFactory harness, TempDirectory repository, params JsonNode?[] items)
        => Validator(harness).Check(items, await harness.ContextLoader.LoadAsync(repository.Path, Token), "r1", Flow);

    /// <summary>A plan whose items each own their own paths, reuse what is there and name what they create passes.</summary>
    [Fact]
    public async Task ASoundPlan_Passes()
    {
        var (harness, repository) = await RepositoryAsync();
        var creates = Item("two", "src/two/**", reuses: null);
        creates["creates"] = new JsonArray(new JsonObject { ["what"] = "a parser", ["why"] = "none parses it", ["searched"] = "src/" });

        Assert.Empty(await CheckAsync(harness, repository, Item("one"), creates));
    }

    /// <summary>Each check of a list, worded once: none, too many, the wrong shape, a name, a name twice, a path owned twice, a forbidden path owned, the lists.</summary>
    [Fact]
    public async Task EachBreachOfAPlan_IsAProblem()
    {
        var (harness, repository) = await RepositoryAsync();

        Assert.Equal([PlanValidator.NoItems], await CheckAsync(harness, repository));
        Assert.Contains(
            "the plan holds 9 work items, and a plan holds at most 8, each one builder's cycle: merge the smallest, or leave some to a later run",
            await CheckAsync(harness, repository, [.. Enumerable.Range(1, 9).Select(at => (JsonNode?)Item($"item-{at}", $"src/{at}/**"))]));

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
        Assert.Empty(Validator(harness).Check([Item("deep", $"src/{deep}/**")], limited, "r1", Flow));
        Assert.Empty(Validator(harness).Check([Item("shallow", "src/a/**")], limited, "r1", built));

        var problem = Assert.Single(Validator(harness).Check([Item("deep", $"src/{deep}/**")], limited, "r1", built));
        Assert.StartsWith("work item 'deep' would work in paths too long for this machine - ", problem, StringComparison.Ordinal);
    }

    /// <summary>The plan check answers ok, the problems and the items, never a failure; given the pieces of an update it fails, saying it does not check them yet.</summary>
    [Fact]
    public async Task ThePlanCheck_AnswersAnOutput_AndRefusesThePiecesOfAnUpdate()
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

        var update = await runner.RunAsync(Run(step, repository.Path, new Dictionary<string, FlowValue> { ["items"] = items, ["scope"] = FlowValue.Of(new JsonArray(new JsonObject { ["kind"] = "added" })) }), Token);
        Assert.False(update.Passed);
        Assert.Equal(HarnessExit.ToolMissing, update.Exit);

        var noUpdate = await runner.RunAsync(Run(step, repository.Path, new Dictionary<string, FlowValue> { ["items"] = items, ["scope"] = FlowValue.Missing }), Token);
        Assert.True(noUpdate.Passed, noUpdate.Error);

        var notPieces = await runner.RunAsync(Run(step, repository.Path, new Dictionary<string, FlowValue> { ["items"] = items, ["scope"] = FlowValue.Of("added") }), Token);
        Assert.Equal(HarnessExit.ConfigInvalid, notPieces.Exit);
        Assert.Equal("'check' is given the pieces of a plan update its scope yields, and it yields no list", notPieces.Error);

        var notAList = await runner.RunAsync(Run(step, repository.Path, new Dictionary<string, FlowValue> { ["items"] = FlowValue.Of("one") }), Token);
        Assert.Equal(HarnessExit.ConfigInvalid, notAList.Exit);
        Assert.Equal("'check' checks the work items its items yields, and it yields no list", notAList.Error);
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

    /// <summary>The agent of a read-only reason step is made with no worktree, by the same making.</summary>
    [Fact]
    public async Task TheMaking_MakesAnAgentWithNoWorktree()
    {
        var (harness, repository) = await RepositoryAsync();
        var layout = await RunAsync(harness, repository);

        var made = await Create(harness).MakeAsync(Run(new WorkflowStep { Name = "think", Type = "reason" }, repository.Path, made: new FlowMadeAgent("a2", "model-one")), noWorktree: true, Token);

        Assert.True(made.Passed, made.Error);
        Assert.True(harness.OrchestrationStore.ReadAgent(layout, "a2")!.Reads);
    }
}
