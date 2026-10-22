using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// What the engine hands a step where it starts (slice 20a): the agent a create-agent step makes, named by the engine
/// with the model it is made for and written to the step's record before its runner is asked, never given twice and
/// named anew once spent; what a validate-plan step's items and scope read and the pull request's base an orient step
/// says; and the run the step belongs to.
/// </summary>
public sealed class FlowStepStartTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>A runner that answers each step by its script, keeping every run it was asked.</summary>
    private sealed class Runner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, FlowStepResult>> _scripts = new(StringComparer.Ordinal);

        /// <summary>Every run asked, in order.</summary>
        public List<FlowStepRun> Runs { get; } = [];

        /// <summary>What the records held of the step when its runner was asked: its record's agent and model.</summary>
        public List<(string? Made, string? Model)> RecordedWhenAsked { get; } = [];

        /// <summary>The records the runner reads back where it is asked, as a step's runner would read step.json.</summary>
        public Records? Records { get; set; }

        public Runner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = script;
            return this;
        }

        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            Runs.Add(run);

            if (Records?.Last(run.Instance) is { } recorded)
            {
                RecordedWhenAsked.Add((recorded.Made, recorded.Model));
            }

            return Task.FromResult(_scripts.GetValueOrDefault(run.Step.Name, _ => FlowStepResult.Ended(passed: true, HarnessExit.Success))(run));
        }

        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    /// <summary>Records every write of a step's snapshot; names agents over the names it is told are on disk.</summary>
    private sealed class Records(params string[] onDisk) : IFlowRecords
    {
        private readonly List<FlowSnapshotStep> _written = [];

        /// <summary>Every name the engine asked to name over, by each ask.</summary>
        public List<IReadOnlyCollection<string>> Asked { get; } = [];

        public void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
        {
            lock (_written)
            {
                if (instance is not null && snapshot.Steps.FirstOrDefault(step => step.Instance == instance) is { } step)
                {
                    _written.Add(step);
                }
            }
        }

        public void Question(IReadOnlyList<FlowQuestionEntry> questions, IReadOnlyList<FlowRefusedAnswer>? refused)
        {
        }

        public string NameAgent(IReadOnlyCollection<string> recorded)
        {
            Asked.Add(recorded);
            return OrchestrationRules.NextAgentName([.. recorded, .. onDisk]);
        }

        /// <summary>The last record written of <paramref name="instance"/>.</summary>
        public FlowSnapshotStep? Last(string instance)
        {
            lock (_written)
            {
                return _written.LastOrDefault(step => step.Instance == instance);
            }
        }
    }

    private static FlowCoordinator Coordinator(WorkflowDefinition definition, Runner runner, IFlowRecords? records = null, IReadOnlyDictionary<string, string>? models = null, IReadOnlyDictionary<string, string>? inputs = null, FlowRunPlace? place = null)
        => new(definition, runner, new NoRunnerFacts(), new JumpingClock(), inputs, records, runName: "r1", place: place, agentModels: models);

    /// <summary>
    /// A create-agent step is named a1 and given the model resolved for it, both written to its record before its runner
    /// is asked, and handed to the runner; the agent is the step's Made once it is done.
    /// </summary>
    [Fact]
    public async Task ACreateAgentStep_IsNamedWithItsModel_InItsRecord_BeforeItsRunnerIsAsked()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: worker.md
            """);
        var records = new Records();
        var runner = new Runner { Records = records }.On("work", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["agent"] = run.Made!.Name }));
        var coordinator = Coordinator(definition, runner, records, new Dictionary<string, string>(StringComparer.Ordinal) { ["work"] = "model-one" });

        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(new FlowMadeAgent("a1", "model-one"), runner.Runs.Single().Made);
        Assert.Equal(("a1", "model-one"), runner.RecordedWhenAsked.Single());
        Assert.Equal("a1", coordinator.Instances["work"].Made);
        Assert.Equal("model-one", records.Last("work")!.Model);
    }

    /// <summary>
    /// The next name is one past the highest any step holds and any agent directory the run has, read by the records: a
    /// directory a8 makes the first agent a9 and the second a10.
    /// </summary>
    [Fact]
    public async Task AgentNames_GoOnePastTheHighest_OfTheStepsAndTheRunsDirectories()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: first
                type: create-agent
                agent: worker.md
              - name: second
                type: create-agent
                agent: worker.md
                dependsOn: [first]
            """);
        var records = new Records("a8", "notes");
        var runner = new Runner();
        var coordinator = Coordinator(definition, runner, records);

        _ = await coordinator.RunAsync(Token);

        Assert.Equal(["a9", "a10"], runner.Runs.Select(run => run.Made!.Name));
        Assert.Equal(["a9"], records.Asked[1]);
    }

    /// <summary>A step run again after an attempt that did not finish is given the same agent: the record names it, and the next call asks for that one.</summary>
    [Fact]
    public async Task AnAttemptRunAgain_IsGivenTheSameAgent()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: worker.md
            """);
        var runner = new Runner().On("work", run => run.Attempt == 1
            ? FlowStepResult.Ended(passed: false, HarnessExit.Incomplete, "made part way")
            : FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["agent"] = run.Made!.Name }));
        var coordinator = Coordinator(definition, runner, new Records());

        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["a1", "a1"], runner.Runs.Select(run => run.Made!.Name));
    }

    /// <summary>A name the runner says is spent is given again to none: the step's record drops it, and its next attempt is named anew.</summary>
    [Fact]
    public async Task ASpentName_IsDropped_AndTheNextAttemptIsNamedAnew()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: worker.md
            """);
        var runner = new Runner().On("work", run => run.Attempt == 1
            ? new FlowStepResult { Asked = null, Passed = false, Exit = HarnessExit.Incomplete, Error = "deleted", NameSpent = true }
            : FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["agent"] = run.Made!.Name }));
        var coordinator = Coordinator(definition, runner);

        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["a1", "a2"], runner.Runs.Select(run => run.Made!.Name));
        Assert.Equal("a2", coordinator.Instances["work"].Made);
    }

    /// <summary>A create-agent step whose output names an agent other than the one the engine named fails, 70: the engine's own fault.</summary>
    [Fact]
    public async Task AnOutputNamingAnotherAgent_FailsTheStep_AsTheEnginesFault()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: worker.md
            """);
        var runner = new Runner().On("work", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["agent"] = "a7" }));
        var coordinator = Coordinator(definition, runner);

        _ = await coordinator.RunAsync(Token);

        var work = coordinator.Instances["work"];
        Assert.Equal(FlowInstanceState.Failed, work.State);
        Assert.Equal(HarnessExit.InternalError, work.Exit);
        Assert.Equal("'work' answered that it made agent 'a7', and the engine named its agent 'a1'", work.Error);
    }

    /// <summary>A validate-plan step is handed what its items and scope read, where it starts; a step of another type, nothing of them.</summary>
    [Fact]
    public async Task AValidatePlanStep_IsHandedWhatItsItemsAndScopeRead()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: check
                type: validate-plan
                dependsOn: [plan]
                items: 'plan.items'
                scope: 'plan.scope'
            """);
        var runner = new Runner().On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: JsonNode.Parse("""{"items": [{"name": "one"}], "scope": []}""")));
        var place = new FlowRunPlace("/repo", "r1", "abc", definition);
        var coordinator = Coordinator(definition, runner, place: place);

        _ = await coordinator.RunAsync(Token);

        var check = runner.Runs.Single(run => run.Instance == "check");
        Assert.Equal("""[{"name":"one"}]""", check.Read!["items"].ToNode()!.ToJsonString());
        Assert.Equal("[]", check.Read["scope"].ToNode()!.ToJsonString());
        Assert.Same(place, check.Place);
        Assert.Empty(runner.Runs.Single(run => run.Instance == "plan").Read!);
    }

    /// <summary>A validate-plan step whose items read nothing fails as an unreadable expression does, and its runner is never asked.</summary>
    [Fact]
    public async Task AValidatePlanStepWhoseItemsReadNothing_FailsUnreadable()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: check
                type: validate-plan
                dependsOn: [plan]
                items: 'plan.items'
            """);
        var runner = new Runner();
        var coordinator = Coordinator(definition, runner);

        _ = await coordinator.RunAsync(Token);

        var check = coordinator.Instances["check"];
        Assert.Equal(FlowInstanceState.Failed, check.State);
        Assert.Equal(HarnessExit.ConfigInvalid, check.Exit);
        Assert.Equal("expression 'items' of 'check': 'plan.items' reads nothing", check.Error);
        Assert.DoesNotContain(runner.Runs, run => run.Instance == "check");
    }

    /// <summary>A validate-plan step whose scope reads nothing is no update: it is handed nothing for it, and runs.</summary>
    [Fact]
    public async Task AValidatePlanStepWhoseScopeReadsNothing_RunsAsNoUpdate()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: check
                type: validate-plan
                dependsOn: [plan]
                items: 'plan.items'
                scope: 'plan.scope'
            """);
        var runner = new Runner().On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: JsonNode.Parse("""{"items": [{"name": "one"}]}""")));
        var coordinator = Coordinator(definition, runner);

        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.True(runner.Runs.Single(run => run.Instance == "check").Read!["scope"].IsMissing);
    }

    /// <summary>An orient step is handed the flow's pull request's base, filled from the run's inputs and its name; with no pull-request base, none.</summary>
    [Fact]
    public async Task AnOrientStep_IsHandedThePullRequestsBase_FilledFromTheInputsAndTheRunsName()
    {
        var definition = Parse("""
            name: flow
            inputs:
              - name: target
                default: develop
            steps:
              - name: orient
                type: orient
              - name: pr
                type: pull-request
                dependsOn: [orient]
                title: the work
                body: body.md
                base: '{input.target}-{run.name}'
            """);
        var runner = new Runner();

        _ = await Coordinator(definition, runner).RunAsync(Token);

        Assert.Equal("develop-r1", runner.Runs.Single(run => run.Instance == "orient").Read!["base"].AsTemplateText());

        var without = Parse("""
            name: flow
            steps:
              - name: orient
                type: orient
            """);
        var bare = new Runner();

        _ = await Coordinator(without, bare).RunAsync(Token);

        Assert.False(bare.Runs.Single().Read!.ContainsKey("base"));
    }

    /// <summary>An orient step whose pull request's base reads nothing fails as an unreadable expression does, and its runner is never asked.</summary>
    [Fact]
    public async Task AnOrientStepWhosePullRequestsBaseReadsNothing_FailsUnreadable()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: orient
                type: orient
              - name: pr
                type: pull-request
                dependsOn: [orient]
                title: the work
                body: body.md
                base: '{input.x}'
            """);
        var runner = new Runner();
        var coordinator = Coordinator(definition, runner);

        _ = await coordinator.RunAsync(Token);

        var orient = coordinator.Instances["orient"];
        Assert.Equal(FlowInstanceState.Failed, orient.State);
        Assert.Equal(HarnessExit.ConfigInvalid, orient.Exit);
        Assert.Equal("expression 'base' of 'orient': '{input.x}' reads nothing", orient.Error);
        Assert.Empty(runner.Runs);
    }

    /// <summary>The next agent's name: one past the highest a&lt;N&gt; there, names of another shape counting for none.</summary>
    [Theory]
    [InlineData(new string[0], "a1")]
    [InlineData(new[] { "a1", "a2" }, "a3")]
    [InlineData(new[] { "a9", "a10", "a2" }, "a11")]
    [InlineData(new[] { "a01", "ab", "b7", "a", "a-1", "A5" }, "a1")]
    public void TheNextAgentName_IsOnePastTheHighest(string[] taken, string next)
        => Assert.Equal(next, OrchestrationRules.NextAgentName(taken));
}
