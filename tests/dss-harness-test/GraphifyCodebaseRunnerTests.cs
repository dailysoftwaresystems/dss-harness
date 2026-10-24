using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DssHarness.Core.Codebase;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Schemas;

namespace DssHarness.Tests;

/// <summary>
/// The graphify-codebase step over a real repository: its output as its type's schema names it, the run's shape of the
/// graph, failOn, what it refuses - and the step anywhere a flow puts it, each instance, visit and retry one more sync.
/// </summary>
public sealed class GraphifyCodebaseRunnerTests : IDisposable
{
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

    /// <summary>
    /// The step syncs the run's graph of the main checkout in the shape the workflow's <c>codebaseGraph</c> says, and
    /// answers the sync as its type's built-in schema names it; a step that says rebuild reads every file again.
    /// </summary>
    [Fact]
    public async Task TheStep_SyncsTheRunsGraph_InTheWorkflowsShape_AndAnswersAsItsSchemaNamesIt()
    {
        var (harness, repository) = await RepositoryAsync();
        repository.WriteFile("docs/guide.md", "# Guide\n\n## Use\n");
        var definition = Parse("""
            name: flow
            codebaseGraph:
              exclude: ["file.txt"]
            steps:
              - name: map
                type: graphify-codebase
              - name: again
                type: graphify-codebase
                rebuild: true
            """);

        var result = await Runner(harness).RunAsync(Run(Step(definition, "map"), repository.Path, definition), Token);
        var again = await Runner(harness).RunAsync(Run(Step(definition, "again"), repository.Path, definition), Token);

        Assert.True(result.Passed, result.Error);
        Assert.Equal(((int)result.Output!["extracted"]!, 0), ((int)again.Output!["extracted"]!, (int)again.Output["reused"]!));
        Assert.True((int)again.Output["extracted"]! > 0);
        var output = result.Output!.AsObject();
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("graphify-codebase")!.Output!)!, output));
        Assert.Equal(OrchestratorLayout.Of(new HarnessLayout(repository.Path, repository.Path), "r1").GraphDirectory, (string?)output["graph"]);
        Assert.Equal(CodebaseGraphService.BaseView, (string?)output["view"]);
        Assert.Equal((await harness.RunGitAsync(repository.Path, ["rev-parse", "HEAD"], Token)).StandardOutput.Trim(), (string?)output["commit"]);
        Assert.True((bool)output["dirty"]!);
        Assert.Contains(output["languages"]!.AsArray(), language => (string?)language!["language"] == "markdown");
        Assert.Contains(harness.OrchestrationStore.ReadGraphSymbols(OrchestratorLayout.Of(new HarnessLayout(repository.Path, repository.Path), "r1"))!.Nodes, node => node.Id == "markdown:docs/guide.md#Guide/Use");
        Assert.DoesNotContain(harness.OrchestrationStore.ReadGraphManifest(OrchestratorLayout.Of(new HarnessLayout(repository.Path, repository.Path), "r1"))!.Files.Keys, path => path == "file.txt");
        Assert.Equal(
            output.Select(member => member.Key).Order(StringComparer.Ordinal),
            ["commit", "dirty", "edges", "extracted", "files", "graph", "languages", "nodes", "removed", "reused", "seconds", "skipped", "view"]);
    }

    /// <summary>
    /// A graph is a map and never red: a file it could not read fails the step (20) only where its <c>failOn:
    /// incomplete</c> says so, naming each - with the output still given - and passes it otherwise.
    /// </summary>
    [Fact]
    public async Task AFileTheGraphCouldNotRead_FailsTheStepOnlyUnderFailOnIncomplete()
    {
        var (harness, repository) = await RepositoryAsync();
        File.WriteAllBytes(Path.Combine(repository.Path, "bad.md"), [0x23, 0x20, 0xE9, 0x0A]);
        var definition = Parse("""
            name: flow
            steps:
              - name: strict
                type: graphify-codebase
                failOn: incomplete
              - name: loose
                type: graphify-codebase
                failOn: never
              - name: plain
                type: graphify-codebase
            """);
        var runner = Runner(harness);

        var strict = await runner.RunAsync(Run(Step(definition, "strict"), repository.Path, definition), Token);
        var loose = await runner.RunAsync(Run(Step(definition, "loose"), repository.Path, definition), Token);
        var plain = await runner.RunAsync(Run(Step(definition, "plain"), repository.Path, definition), Token);

        Assert.Equal((false, false, HarnessExit.CommandFailed), (strict.Passed, strict.Red, strict.Exit));
        Assert.StartsWith("the graph has 1 file(s) it could not read, and the step says failOn: incomplete: bad.md (it ", strict.Error, StringComparison.Ordinal);
        Assert.NotNull(strict.Output);
        Assert.Equal((true, true), (loose.Passed, plain.Passed));
    }

    /// <summary>
    /// With in, the step syncs the view of the worktree of the agent its create-agent made, held against the base - synced
    /// first where the run has none - as an overlay of each file the worktree holds otherwise and each of the base's it does
    /// not hold, its output's view naming the agent; an agent that only reads has the base for its view, and an agent with
    /// no worktree to list is refused (13), syncing nothing of it.
    /// </summary>
    [Fact]
    public async Task AStepNamingAnAgentsTree_SyncsThatAgentsView_HeldAgainstTheBase()
    {
        var temp = new TempDirectory();
        _made.Add(temp);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("worker");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(worktree, Path.Combine("docs", "new.md"), "# New\n");
        File.Delete(Path.Combine(worktree, "a.txt"));
        await kit.CreateReadingAgentAsync("reader");
        var definition = Parse("""
            name: flow
            steps:
              - name: make
                type: create-agent
                agent: worker
              - name: map
                type: graphify-codebase
                in: make
                dependsOn: [make]
            """);
        var step = Step(definition, "map");

        var viewed = await Runner(kit.Harness).RunAsync(Run(step, kit.Main, definition, OrchestrationKit.Orchestrator) with { WorksIn = "worker" }, Token);
        var read = await Runner(kit.Harness).RunAsync(Run(step, kit.Main, definition, OrchestrationKit.Orchestrator) with { WorksIn = "reader" }, Token);
        var none = await Runner(kit.Harness).RunAsync(Run(step, kit.Main, definition, OrchestrationKit.Orchestrator) with { WorksIn = "nobody" }, Token);

        Assert.True(viewed.Passed, viewed.Error);
        Assert.Equal("worker", (string?)viewed.Output!["view"]);
        var view = kit.Harness.OrchestrationStore.ReadGraphView(kit.Layout, "worker")!;
        Assert.Equal(kit.Harness.OrchestrationStore.ReadGraphManifest(kit.Layout)!.Digest, view.Base);
        Assert.Equal(["b.txt", "docs/new.md"], view.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["a.txt"], view.Deleted);
        Assert.Contains(kit.Harness.OrchestrationStore.ReadGraphManifest(kit.Layout)!.Files.Keys, path => path == "a.txt");
        Assert.True(read.Passed, read.Error);
        Assert.Equal(CodebaseGraphService.BaseView, (string?)read.Output!["view"]);
        Assert.Equal((false, HarnessExit.Refused), (none.Passed, none.Exit));
        Assert.EndsWith("of 'o1', which has no worktree to list. Nothing was synced.", none.Error, StringComparison.Ordinal);
        Assert.Equal(["worker"], kit.Harness.OrchestrationStore.GraphViews(kit.Layout));
    }

    /// <summary>A graph whose longest path would not fit under the run's directory is refused before anything is synced.</summary>
    [Fact]
    public async Task AGraphWhosePathsWouldNotFit_IsRefused_BeforeAnySync()
    {
        var (harness, repository) = await RepositoryAsync();
        var graph = OrchestratorLayout.Of(new HarnessLayout(repository.Path, repository.Path), "r1").GraphDirectory;
        harness.WriteConfig(repository.Path, new HarnessConfig { Worktrees = new WorktreeSettings { PathLimit = graph.Length + 40 } });
        var definition = Parse("""
            name: flow
            steps:
              - name: map
                type: graphify-codebase
            """);

        var result = await Runner(harness).RunAsync(Run(Step(definition, "map"), repository.Path, definition), Token);

        Assert.Equal((false, HarnessExit.Refused), (result.Passed, result.Exit));
        Assert.StartsWith("the run's codebase graph keeps paths of ", result.Error, StringComparison.Ordinal);
        Assert.EndsWith(" Nothing was synced.", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(graph));
    }

    /// <summary>
    /// The step runs anywhere a flow puts it - at the top, in a group, in each iteration of a forEach over its own output's
    /// languages, in a repeat until the tree is clean, as a handler of another's success, in the recovery, and again when
    /// its first attempt ended incomplete - each one more sync of the one graph, none of which reads a file twice.
    /// </summary>
    [Fact]
    public async Task TheStep_RunsAnywhereAFlowPutsIt_EachOneMoreSyncOfTheOneGraph()
    {
        var (harness, repository) = await RepositoryAsync();
        repository.WriteFile("docs/guide.md", "# Guide\n");
        await harness.CommitAllAsync(repository.Path, "guide", Token);
        var definition = Parse("""
            name: flow
            codebaseGraph:
              languages: [markdown]
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: remap
                  type: graphify-codebase
            steps:
              - name: map
                type: graphify-codebase
              - name: around
                dependsOn: [map]
                steps:
                  - name: inner
                    type: graphify-codebase
              - name: each
                forEach: map.languages
                dependsOn: [map]
                steps:
                  - name: per
                    type: graphify-codebase
              - name: rounds
                dependsOn: [map]
                repeat:
                  until: "!round.dirty"
                  maxIterations: 3
                steps:
                  - name: round
                    type: graphify-codebase
              - name: told
                type: graphify-codebase
                on: {step: map, outcome: success}
              - name: build
                type: orient
                dependsOn: [map]
                onFailure: recover
              - name: flaky
                type: graphify-codebase
                dependsOn: [build]
            """);
        Assert.Empty(WorkflowValidator.Validate(definition, hasCommitTemplate: false, files: null, languages: CodeGraphExtractors.OfThisBuild.Languages));
        var runner = new GraphOrScripted(Runner(harness));
        var place = new FlowRunPlace(repository.Path, "r1", null, definition);
        var coordinator = new FlowCoordinator(definition, runner, new NoWrites(), TimeProvider.System, runName: "r1", place: place);

        var end = await coordinator.RunAsync(Token);

        Assert.True(end.Status == FlowRunStatus.Done, string.Join(", ", coordinator.Instances.Select(instance => $"{instance.Key}={instance.Value.State}")) + " | " + string.Join(" / ", coordinator.OpenQuestions.Select(question => question.Problem)) + " | " + string.Join(", ", runner.Graphed.Select(graphed => $"{graphed.Instance}:{graphed.Output?["dirty"]}")));
        Assert.Equal(
            ["around.inner", "each[0].per", "each[1].per", "flaky", "flaky", "map", "recover.remap", "rounds#1.round", "told"],
            runner.Graphed.Select(graphed => graphed.Instance).Order(StringComparer.Ordinal));
        Assert.All(runner.Graphed.Where(graphed => graphed.Output is not null), graphed => Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("graphify-codebase")!.Output!)!, graphed.Output)));

        var lines = File.ReadAllLines(OrchestratorLayout.Of(new HarnessLayout(repository.Path, repository.Path), "r1").GraphSyncFile);
        Assert.Equal(8, lines.Length);
        Assert.Single(lines, line => !line.Contains("\"extracted\":0", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\"why\":\"each[1].per\"", StringComparison.Ordinal));
    }

    /// <summary>A repository with the harness initialised by <paramref name="config"/>, committed.</summary>
    private async Task<(HarnessFactory Harness, TempDirectory Repository)> RepositoryAsync(HarnessConfig? config = null)
    {
        var harness = new HarnessFactory();
        var repository = new TempDirectory();
        _made.Add(repository);

        repository.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(repository.Path, Token, config);
        await harness.CommitAllAsync(repository.Path, "initial", Token);

        return (harness, repository);
    }

    private static WorkflowDefinition Parse(string text) => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    private static WorkflowStep Step(WorkflowDefinition definition, string name) => definition.Steps.Single(step => step.Name == name);
    private static GraphifyCodebaseRunner Runner(HarnessFactory harness)
        => new(
            harness.ContextLoader,
            harness.Platform,
            new CodebaseGraphService(harness.GitClient, harness.FileSystem, harness.OrchestrationStore, CodeGraphExtractors.OfThisBuild, harness.Platform, new RunningToolDouble(), harness.Output, TimeProvider.System),
            harness.PathBudget,
            CodeGraphExtractors.OfThisBuild,
            harness.OrchestrationStore);

    private static FlowStepRun Run(WorkflowStep step, string repository, WorkflowDefinition definition, string run = "r1")
        => new(step, step.Name, 1, FlowValue.Missing, null, null, [], new NoEvents(), Place: new FlowRunPlace(repository, run, null, definition), Read: new Dictionary<string, FlowValue>());

    /// <summary>
    /// The real graph step for every graphify-codebase step - its first attempt of <c>flaky</c> ending incomplete (21), as a
    /// sync a git that did not answer leaves - and <c>build</c> failing its first time, every other step passing.
    /// </summary>
    private sealed class GraphOrScripted(GraphifyCodebaseRunner graph) : IFlowStepRunner
    {
        private int _flaky;
        private int _build;

        /// <summary>Every graph step asked, with what it answered.</summary>
        public ConcurrentQueue<(string Instance, JsonNode? Output)> Graphed { get; } = new();

        /// <inheritdoc/>
        public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            if (run.Step.Name == "build")
            {
                return Interlocked.Increment(ref _build) == 1
                    ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "it failed once")
                    : FlowStepResult.Ended(passed: true, HarnessExit.Success);
            }

            if (run.Step.Type != "graphify-codebase")
            {
                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            }

            if (run.Step.Name == "flaky" && Interlocked.Increment(ref _flaky) == 1)
            {
                Graphed.Enqueue((run.Instance, null));
                return FlowStepResult.Ended(passed: false, HarnessExit.Incomplete, error: "git did not answer in time");
            }

            var result = await graph.RunAsync(run, cancellationToken);
            Graphed.Enqueue((run.Instance, result.Output));

            // A repeat's iteration reads its output: the sync's own, a clean tree's dirty false.
            return result;
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class NoWrites : IFlowRunnerFacts
    {
        /// <inheritdoc/>
        public bool WritesTree(string runner) => false;
    }

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
