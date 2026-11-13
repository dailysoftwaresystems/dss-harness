using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// A workflow used as a step: the resolution that reads the called workflow and places its steps in the step's own,
/// the boundary that makes its steps read only their inputs and their own, the outputs a later step reads as the use's
/// own, and the recovery that a failure inside it reaches first - in memory over fake step runners.
/// </summary>
public sealed class FlowUseTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests run one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>
    /// A pipeline a use names: two steps over its inputs, one declared output, and a recovery of its own - every kind
    /// of thing a used workflow brings. Its verify step gives its failures to the pipeline's own recovery, as the
    /// shipped one's steps give theirs to the run's.
    /// </summary>
    private const string Pipeline =
        """
        name: pipeline
        inputs:
          - name: item
            kind: value
          - name: title
            kind: text
            required: true
          - name: prompt
            kind: file
          - name: failTo
            kind: step
            default: continue
        params:
          - name: tone
            default: plain
        outputs:
          - name: changed
            from: implement.latest.changed
        steps:
          - name: implement
            type: reason
            agent: worker
            prompt: prompts/implement.md
            outputs: schemas/implement.json
          - name: verify
            type: orient
            dependsOn: [implement]
            retries: 1
            onFailure: recover
        recover:
            onSuccess: failed-step
            onFailure: pause
            steps:
              - name: diagnose
                type: orient
        """;

    /// <summary>The definition of a caller that uses the pipeline, resolved against a reader that reads the pipeline's text.</summary>
    private static WorkflowDefinition Resolved(string caller, string? pipeline = null)
        => ResolvedWithProblems(caller, pipeline).Definition;

    /// <summary>
    /// One change to the pipeline's text, read with single newlines whatever endings the checkout gave this file, and
    /// refused where the text holds no <paramref name="from"/>: a swap that changed nothing would let a test pass or
    /// wait over the fixture it meant to change.
    /// </summary>
    private static string Swap(string text, string from, string to)
    {
        var lines = text.ReplaceLineEndings("\n");
        var found = from.ReplaceLineEndings("\n");
        Assert.Contains(found, lines, StringComparison.Ordinal);
        return lines.Replace(found, to.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    /// <summary>The resolution and its problems, for the tests that refuse.</summary>
    private static (WorkflowDefinition Definition, IReadOnlyList<string> Problems) ResolvedWithProblems(string caller, string? pipeline = null)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["flow/pipeline/pipeline.yml"] = pipeline ?? Pipeline,
        };

        return WorkflowResolver.Resolve(Parse(caller), "flow", path => files.GetValueOrDefault(path));
    }

    /// <summary>
    /// WF1: a step that says <c>use:</c> runs the called workflow inside the run - its steps under the step's own name,
    /// in declaration order, as a group's members are - and the caller goes on from them.
    /// </summary>
    [Fact]
    public async Task AStepThatUsesAWorkflow_RunsItsStepsInsideTheRun()
    {
        var runner = new FakeRunner();
        var definition = Resolved("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: 'run {run.name}'
                  prompt: prompts/implement.md
              - name: after
                type: orient
                dependsOn: [items]
            """);

        var coordinator = new FlowCoordinator(definition, runner, Facts(), new JumpingClock());
        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["items.implement", "items.verify", "after"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items.implement"].State);
    }

    /// <summary>
    /// The texts a used workflow declares are carried to its use and read inside it by their name: a step of the
    /// called workflow's prompt reads <c>{text.&lt;name&gt;}</c> as the called workflow declares it first, and a name
    /// only the caller declares reaches its steps through the use, as the run's own texts do; a name neither declares
    /// is refused inside it, naming the called workflow.
    /// </summary>
    [Fact]
    public void TheTextsAUsedWorkflowDeclares_AreItsStepsOwnInsideIt()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["flow/pipeline/prompts/implement.md"] = "Close the class:\n{text.closure}\n",
            ["flow/pipeline/prompts/shared.md"] = "The caller's bar:\n{text.barred}\n",
            ["flow/pipeline/prompts/absent.md"] = "Nothing declares this:\n{text.nope}\n",
            ["flow/pipeline/texts/closure.md"] = "the pipeline's own universe\n",
        };

        var caller = """
            name: flow
            texts:
              barred: texts/barred.md
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: the work
            """;

        string? Read(string path)
            => files.TryGetValue(path, out var text) ? text : null;

        // The called workflow's own text is carried to its use; a name only the caller declares reaches its steps
        // through it, for the run's texts are what a use declares none for - as its inputs are not.
        var declared = WorkflowResolver.Resolve(
            Parse(caller),
            "flow",
            path => path == "flow/pipeline/pipeline.yml"
                ? Swap(Pipeline, "prompt: prompts/implement.md", "prompt: prompts/shared.md")
                : Read(path));

        Assert.Empty(declared.Problems);
        Assert.Null(declared.Definition.Steps[0].Use!.Texts);
        Assert.Empty(WorkflowValidator.Validate(declared.Definition, hasCommitTemplate: false, Files));

        var own = WorkflowResolver.Resolve(
            Parse(caller),
            "flow",
            path => path == "flow/pipeline/pipeline.yml"
                ? Swap(Pipeline, "name: pipeline\n", "name: pipeline\ntexts:\n  closure: texts/closure.md\n")
                : Read(path));

        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["closure"] = "texts/closure.md" },
            own.Definition.Steps[0].Use!.Texts);
        Assert.Empty(WorkflowValidator.Validate(own.Definition, hasCommitTemplate: false, Files));

        // A name neither the called workflow nor the run declares is refused inside the called one, naming it.
        var absent = WorkflowResolver.Resolve(
            Parse(caller),
            "flow",
            path => path == "flow/pipeline/pipeline.yml"
                ? Swap(Pipeline, "prompt: prompts/implement.md", "prompt: prompts/absent.md")
                : Read(path));

        Assert.Equal(
            ["step 'items.implement' prompt 'prompts/absent.md' line 2 reads 'text.nope', and the workflow 'flow/pipeline' declares no text 'nope'"],
            WorkflowValidator.Validate(absent.Definition, hasCommitTemplate: false, Files));
        return;

        string? Files(string path, string? owner)
            => owner is null ? Read(path) : Read($"flow/pipeline/{path}");
    }

    /// <summary>
    /// WF1, the with values: a <c>value</c> is read in the use's scope - the item of a forEach among what it may read -
    /// a <c>text</c> is filled per item, and the called workflow's steps read them as <c>input.&lt;name&gt;</c>; its
    /// params are its own defaults, never the calling run's.
    /// </summary>
    [Fact]
    public async Task TheWithValues_AreReadAsTheirInputsDeclare_AndReadInsideAsInput()
    {
        var pipeline = Swap(Pipeline, 
            "  - name: implement\n    type: reason\n    agent: worker\n    prompt: prompts/implement.md\n    outputs: schemas/implement.json\n",
            "  - name: implement\n    type: reason\n    agent: worker\n    prompt: prompts/implement.md\n    outputs: schemas/implement.json\n    when: 'input.item.kind == item.kind && input.title == \"fix: the work\" && param.tone == \"plain\"'\n");
        var runner = new FakeRunner().On("listing", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["list"] = new JsonArray(new JsonObject { ["kind"] = "fix" }, new JsonObject { ["kind"] = "test" }) }));
        var definition = Resolved("""
            name: flow
            steps:
              - name: listing
                type: orient
              - name: items
                use: ./pipeline/pipeline.yml
                forEach: listing.list
                dependsOn: [listing]
                with:
                  item: item
                  title: '{item.kind}: the work'
                  prompt: prompts/implement.md
            """, pipeline);

        var coordinator = new FlowCoordinator(definition, runner, Facts(), new JumpingClock());
        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The first item's implement step reads its own item, its filled title and the pipeline's own param: its when
        // holds and it runs; the second item's title fills differently, and its implement is skipped - each item's
        // with values are its own, and the verifiers of both items run.
        Assert.Equal(["listing", "items[0].implement"], runner.Started.Take(2).ToList());
        Assert.Contains("items[0].verify", runner.Started);
        Assert.Contains("items[1].verify", runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items[0].implement"].State);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["items[1].implement"].State);
    }

    /// <summary>
    /// WF1, the boundary at run time: a step of a used workflow reads the calling run's input never - its input is the
    /// one the use filled, and where both hold a name, the filled one is what its steps read.
    /// </summary>
    [Fact]
    public async Task AUsedWorkflowsStep_ReadsTheRunsInputNever()
    {
        var pipeline = Swap(Pipeline, 
            "  - name: implement\n    type: reason\n    agent: worker\n    prompt: prompts/implement.md\n    outputs: schemas/implement.json\n",
            "  - name: implement\n    type: reason\n    agent: worker\n    prompt: prompts/implement.md\n    outputs: schemas/implement.json\n    when: 'input.title == \"the filled one\"'\n");
        var runner = new FakeRunner();
        var definition = Resolved("""
            name: flow
            inputs:
              - name: title
                kind: text
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: the filled one
                  prompt: prompts/implement.md
            """, pipeline);

        var coordinator = new FlowCoordinator(definition, runner, Facts(), new JumpingClock(), inputs: new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "the run's own" });
        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The run was started with another text of the very name the called step reads, and its input is not the run's:
        // the use filled 'the filled one', the step's when holds on that alone, and it runs.
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items.implement"].State);
    }

    /// <summary>
    /// WF1, the outputs: a used workflow's declared outputs are the step's own once it ends, read by a later step as
    /// any step's are.
    /// </summary>
    [Fact]
    public async Task TheOutputsAUsedWorkflowDeclares_AreTheStepsOwnOnceItEnds()
    {
        var runner = new FakeRunner()
            .On("implement", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["changed"] = new JsonArray("src/a.cs") }));
        var definition = Resolved("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
              - name: after
                type: orient
                dependsOn: [items]
                when: 'items.changed[0] == "src/a.cs"'
            """);

        var coordinator = new FlowCoordinator(definition, runner, Facts(), new JumpingClock());
        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items"].State);
        Assert.Equal(["items.implement", "items.verify", "after"], runner.Started);
    }

    /// <summary>
    /// WF1, the recovery: a failure inside a used workflow reaches the recovery the called workflow declares - a visit
    /// in the scope the failure stood in, declared under the use - before the caller's handling of the step, and the
    /// failed step runs again once the visit is done.
    /// </summary>
    [Fact]
    public async Task AFailureInsideAUsedWorkflow_ReachesItsOwnRecoveryFirst()
    {
        var failures = 0;
        var runner = new FakeRunner();
        runner.On("verify", () => ++failures == 1
            ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed)
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var definition = Resolved("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: root-diagnose
                  type: orient
            """);

        var coordinator = new FlowCoordinator(definition, runner, Facts(), new JumpingClock());
        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["items.implement", "items.verify", "items.recover.diagnose", "items.verify+2"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items.verify+2"].State);
    }

    /// <summary>
    /// WF1: where the called workflow holds no recovery of its own, a failure a step of it gives to one reaches the
    /// caller's - the use's own <c>onFailure: recover</c>, here, which gives it to the run's, under the use.
    /// </summary>
    [Fact]
    public async Task WhereTheUsedWorkflowHoldsNoRecovery_TheCallersHandlingOfTheStepTakesIt()
    {
        var pipeline = Pipeline.Substring(0, Pipeline.IndexOf("\nrecover:", StringComparison.Ordinal));
        var failures = 0;
        var runner = new FakeRunner();
        runner.On("verify", () => ++failures == 1
            ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed)
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var definition = Resolved("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                onFailure: recover
                with:
                  title: t
                  prompt: prompts/implement.md
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: root-diagnose
                  type: orient
            """, pipeline);

        var coordinator = new FlowCoordinator(definition, runner, Facts(), new JumpingClock());
        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["items.implement", "items.verify", "items.recover.root-diagnose", "items.verify+2"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items"].State);
    }

    /// <summary>
    /// WF1, the step-kind input: a called step's <c>onFailure</c> read from <c>{input.failTo}</c> is filled where the
    /// failure happened, with what the use filled - and the failure goes there.
    /// </summary>
    [Fact]
    public async Task ACalledStepsOnFailure_ReadsTheStepTheUseFilledItWith()
    {
        var pipeline = Swap(Pipeline, "    onFailure: recover\n", "    onFailure: '{input.failTo}'\n");
        var runner = new FakeRunner()
            .On("verify", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed));
        var definition = Resolved("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
                  failTo: pause
            """, pipeline);

        var coordinator = new FlowCoordinator(definition, runner, Facts(), new JumpingClock());
        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The failure pauses the step, as the use filled it: the pipeline's own recovery is never raised, and neither
        // is any the caller holds.
        Assert.Equal(["items.implement", "items.verify"], runner.Started);
        Assert.Equal(FlowInstanceState.Paused, coordinator.Instances["items.verify"].State);
    }

    /// <summary>
    /// WF1, the boundary in the definition: a step of a used workflow that reads a step of the workflow that called it
    /// is refused, and an input the called workflow does not declare is refused with the workflow's own name.
    /// </summary>
    [Fact]
    public void AUsedWorkflowsStep_ReadsOnlyItsOwnStepsAndInputsInTheDefinition()
    {
        var pipeline = Swap(Pipeline, 
            "  - name: verify\n    type: orient\n    dependsOn: [implement]\n    retries: 1\n    onFailure: recover\n",
            "  - name: verify\n    type: orient\n    dependsOn: [implement]\n    retries: 1\n    onFailure: recover\n    when: 'listing.done'\n");
        var (definition, problems) = ResolvedWithProblems("""
            name: flow
            steps:
              - name: listing
                type: orient
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
            """, pipeline);

        Assert.Empty(problems);

        var read = WorkflowValidator.Validate(definition, hasCommitTemplate: false);

        Assert.Equal(
            ["'items.verify.when' reads 'listing.done', and a workflow used as a step reads only its own steps and its own names: 'listing' is of the workflow that called it. Give the fact to the called workflow as one of its inputs"],
            read);

        var unknown = Swap(Pipeline, 
            "  - name: implement\n    type: reason\n    agent: worker\n    prompt: prompts/implement.md\n    outputs: schemas/implement.json\n",
            "  - name: implement\n    type: reason\n    agent: worker\n    prompt: prompts/implement.md\n    outputs: schemas/implement.json\n    when: 'input.wanted == \"yes\"'\n");
        var (_, none) = ResolvedWithProblems("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
            """, unknown);

        Assert.Empty(none);

        var refused = WorkflowValidator.Validate(ResolvedWithProblems("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
            """, unknown).Definition, hasCommitTemplate: false);

        Assert.Equal(
            ["'items.implement.when' reads 'input.wanted', and the workflow 'flow/pipeline' declares no input 'wanted'"],
            refused);
    }

    /// <summary>
    /// WF1, the with against the called workflow's inputs: a name no input of it declares is refused, a required input
    /// the use names none for is refused, and a step-kind value that is neither a policy nor a step the use sees is
    /// refused - a policy naming a recovery that neither workflow declares, among them.
    /// </summary>
    [Fact]
    public void TheWithValues_AreHeldToTheInputsOfTheWorkflowTheyFill()
    {
        var (filled, problems) = ResolvedWithProblems("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: ../beyond.md
                  failTo: recover
            """);

        Assert.Empty(problems);

        Assert.Equal(
            [
                "step 'items' fills 'extra' with, and the workflow 'flow/pipeline' it uses declares no input of that name",
                "step 'items' names no 'title' in with, and the workflow 'flow/pipeline' it uses requires the input",
            ],
            WorkflowValidator.Validate(Resolved("""
                name: flow
                steps:
                  - name: items
                    use: ./pipeline/pipeline.yml
                    with:
                      prompt: prompts/implement.md
                      extra: nope
                """), hasCommitTemplate: false));

        Assert.Equal(
            ["step 'items'.with.prompt is '../beyond.md', which is no path relative to the directory of the workflow 'flow/pipeline' it uses; a file input is such a path, read inside that directory"],
            WorkflowValidator.Validate(filled, hasCommitTemplate: false));

        Assert.Equal(
            [
                "step 'items.verify' gives its failure to the recovery (onFailure: recover), and neither the workflow nor any workflow it uses around it declares a recover; declare one, or say what the failure does",
                "step 'items'.with.failTo is 'recover', and neither the workflow 'flow/pipeline' it uses nor the calling workflow declares a recover; one of them must, for the failures it hands there",
            ],
            WorkflowValidator.Validate(Resolved("""
                name: flow
                steps:
                  - name: items
                    use: ./pipeline/pipeline.yml
                    with:
                      title: t
                      prompt: prompts/implement.md
                      failTo: recover
                """, Pipeline.Substring(0, Pipeline.IndexOf("\nrecover:", StringComparison.Ordinal))), hasCommitTemplate: false));
    }

    /// <summary>V57: a step names a workflow to use and holds no steps of its own, and no type beside it.</summary>
    [Fact]
    public void AStepThatUsesAWorkflow_HoldsNoStepsAndNamesNoType()
    {
        Assert.Equal(
            [
                "line 3, column 5: items names the type 'orient' and a workflow to use; a typed step runs one thing, and the workflow it would use brings steps of its own.",
                "line 6, column 5: other holds steps and names a workflow to use; the workflow it uses brings its own steps, and a step that holds steps uses none.",
            ],
            ParserProblems("""
                name: flow
                steps:
                  - name: items
                    type: orient
                    use: ./pipeline/pipeline.yml
                  - name: other
                    use: ./pipeline/pipeline.yml
                    steps:
                      - name: inner
                        type: orient
                """));

        Assert.Equal(
            [
                "line 3, column 5: lone hands values with 'with', and names no workflow to use; the values are what a used workflow's inputs are filled with.",
                "line 3, column 5: lone names no type and holds no steps; a step does one thing, and a group holds steps of their own.",
            ],
            ParserProblems("""
                name: flow
                steps:
                  - name: lone
                    with:
                      item: item
                """));
    }

    /// <summary>
    /// V62: a failure policy read from an input is a used workflow's alone - a step of the calling workflow that reads
    /// one is refused, and a template that is not one whole read of a step-kind input is refused with the input's shape.
    /// </summary>
    [Fact]
    public void APolicyReadFromAnInput_IsAUsedWorkflowsAlone()
    {
        var direct = WorkflowValidator.Validate(Parse("""
            name: flow
            steps:
              - name: lone
                type: orient
                onFailure: '{input.failTo}'
            """), hasCommitTemplate: false);

        Assert.Equal(
            ["step 'lone' reads its onFailure from '{input.failTo}', and only a step of a workflow used as a step does: a step of the workflow itself says one of 'stop', 'pause', 'continue', 'recover'"],
            direct);

        var broken = Swap(Pipeline, "    onFailure: recover\n", "    onFailure: '{input.failTo} and more'\n");
        var (_, problems) = ResolvedWithProblems("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
                  failTo: pause
            """, broken);

        Assert.Empty(problems);

        Assert.Equal(
            ["step 'items.verify' reads its onFailure from '{input.failTo} and more', which is not one read of a step-kind input; it is {input.<name>} whole, the name of an input the workflow 'flow/pipeline' declares of the kind 'step'"],
            WorkflowValidator.Validate(Resolved("""
                name: flow
                steps:
                  - name: items
                    use: ./pipeline/pipeline.yml
                    with:
                      title: t
                      prompt: prompts/implement.md
                      failTo: pause
                """, broken), hasCommitTemplate: false));
    }

    /// <summary>V58: a workflow that uses itself, directly or through others, is refused with the whole chain.</summary>
    [Fact]
    public void AWorkflowThatUsesItself_IsRefusedWithTheChain()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["flow/inner/inner.yml"] = "name: inner\nsteps:\n  - name: outer\n    use: ../outer/outer.yml\n",
            ["flow/outer/outer.yml"] = "name: outer\nsteps:\n  - name: inner\n    use: ../inner/inner.yml\n",
        };

        var (definition, problems) = WorkflowResolver.Resolve(
            Parse("name: flow\nsteps:\n  - name: outer\n    use: ./outer/outer.yml\n"),
            "flow",
            path => files.GetValueOrDefault(path));

        var problem = Assert.Single(problems);

        Assert.StartsWith("step 'outer' uses '../outer/outer.yml', the workflow 'flow/outer', which is already in use around it: flow/outer -> flow/inner -> flow/outer -> flow/outer.", problem, StringComparison.Ordinal);
    }

    /// <summary>V59: nesting counts across workflows, the reader's own bound of ten levels in all.</summary>
    [Fact]
    public void NestingCountsAcrossWorkflows_UnderTheReadersOwnBound()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var depth = 0; depth <= WorkflowFileParser.MaxDepth; depth++)
        {
            files[$"flow/w{depth}/w{depth}.yml"] = depth == WorkflowFileParser.MaxDepth
                ? "name: w\nsteps:\n  - name: leaf\n    type: orient\n"
                : $"name: w\nsteps:\n  - name: inner\n    use: ../w{depth + 1}/w{depth + 1}.yml\n";
        }

        var (_, problems) = WorkflowResolver.Resolve(
            Parse("name: flow\nsteps:\n  - name: outer\n    use: ./w0/w0.yml\n"),
            "flow",
            path => files.GetValueOrDefault(path));

        var problem = Assert.Single(problems);

        Assert.StartsWith("step 'inner' uses", problem, StringComparison.Ordinal);
        Assert.Contains($"workflows nest at most {WorkflowFileParser.MaxDepth} deep in all", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// WF1: the resolution is what the run freezes and a resume is refused by - the same caller over a changed called
    /// file resolves to another digest, and the rule that refuses a changed definition refuses it; the same file, the
    /// same bytes.
    /// </summary>
    [Fact]
    public void AChangedCalledFile_ChangesTheDigestAResumeIsRefusedBy()
    {
        var caller = """
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
            """;

        var before = FlowRunContext.DigestOf(Resolved(caller));

        Assert.Equal(before, FlowRunContext.DigestOf(Resolved(caller)));
        Assert.NotEqual(before, FlowRunContext.DigestOf(Resolved(caller, pipeline: Swap(Pipeline, "- name: diagnose", "- name: healing"))));
    }

    /// <summary>
    /// WF1: the resolution records what it read - the called workflow's name, the source as written, and the
    /// fingerprint of its file - and the run-level keys of the called one are said as not applied, never taken.
    /// </summary>
    [Fact]
    public void TheResolutionRecordsTheCalledWorkflow_AndSaysWhatItDoesNotApply()
    {
        var pipeline = "name: pipeline\nbudget:\n  usd: 5\ninputs:\n  - name: item\n    kind: value\nsteps:\n  - name: implement\n    type: orient\n";
        var (definition, problems) = ResolvedWithProblems("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  item: item
            """, pipeline);

        Assert.Empty(problems);

        var use = definition.Steps[0].Use!;

        Assert.Equal("./pipeline/pipeline.yml", use.Source);
        Assert.Equal("flow/pipeline", use.Name);
        Assert.Equal(64, use.Fingerprint.Length);
        Assert.Equal(["budget"], use.NotApplied);
        Assert.Equal("implement", definition.Steps[0].Steps[0].Name);
        Assert.Equal("flow/pipeline", definition.Steps[0].Steps[0].WorkflowOf);
    }

    /// <summary>
    /// WF1: a use that names no workflow file - one that leaves the workflows root, one that is not there - is
    /// refused; a called workflow that requires a param no use can fill is refused too.
    /// </summary>
    [Fact]
    public void AUseThatNamesNoWorkflowFile_OrAnUnfillableParam_IsRefused()
    {
        var (definition, problems) = ResolvedWithProblems("name: flow\nsteps:\n  - name: items\n    use: ../../beyond.yml\n");

        Assert.Null(definition.Steps[0].Use);
        Assert.Equal(
            ["step 'items' uses '../../beyond.yml', which is no path to a workflow file: a use is a path relative to the calling workflow's directory, written with forward slashes, that resolves inside the repository's workflows directory"],
            problems);

        var (_, missing) = ResolvedWithProblems("name: flow\nsteps:\n  - name: items\n    use: ./absent/absent.yml\n");

        Assert.Equal(["step 'items' uses './absent/absent.yml', and no workflow file is at 'flow/absent/absent.yml' under the repository's workflows directory"], missing);

        var pipeline = "name: pipeline\nparams:\n  - name: tone\n    required: true\nsteps:\n  - name: implement\n    type: orient\n";
        var (_, unfillable) = ResolvedWithProblems("name: flow\nsteps:\n  - name: items\n    use: ./pipeline/pipeline.yml\n", pipeline);

        Assert.Equal(["step 'items' uses the workflow 'flow/pipeline', whose param 'tone' is required and declares no default: a used workflow is given nothing but its inputs, so it holds the param's own value or none"], unfillable);
    }

    /// <summary>
    /// WF1: a run's own inputs are text or file; value and step are a used workflow's, filled by the with of the step
    /// that uses it, and a definition that declares one is refused.
    /// </summary>
    [Fact]
    public void ARunsOwnInputs_AreTextOrFile_NeverValueOrStep()
    {
        var (_, problems) = ResolvedWithProblems("""
            name: flow
            inputs:
              - name: plan
                kind: value
            steps:
              - name: orient
                type: orient
            """);

        Assert.Equal(
            ["input 'plan' is of the kind 'value', which a run is never started with: a workflow's own inputs are 'text' or 'file', and 'value' and 'step' are a used workflow's, filled by the with of the step that uses it"],
            problems);
    }

    /// <summary>
    /// V63: the file a reason step is asked with is read from one of the used workflow's file-kind inputs alone - a
    /// step of the calling workflow that reads one is refused, and a template that is not one whole read of such an
    /// input, or a use that fills it with no path, is refused with the input's shape.
    /// </summary>
    [Fact]
    public void TheFileAStepIsAskedWith_IsReadFromOneInputOfTheUsedWorkflow()
    {
        var gated = Swap(Pipeline, "    prompt: prompts/implement.md\n", "    prompt: '{input.prompt}'\n");

        Assert.Empty(WorkflowValidator.Validate(Resolved("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
            """, gated), hasCommitTemplate: false, (named, owner) => named.StartsWith("agents/", StringComparison.Ordinal) ? null : named));

        Assert.Equal(
            ["step 'lone' is asked with '{input.prompt}', and only a step of a workflow used as a step reads its prompt so: it is {input.<name>} whole, the name of an input the used workflow declares of the kind 'file'"],
            WorkflowValidator.Validate(Parse("""
                name: flow
                steps:
                  - name: lone
                    type: reason
                    agent: worker
                    prompt: '{input.prompt}'
                    outputs: schemas/lone.json
                """), hasCommitTemplate: false));

        Assert.Equal(
            ["step 'items.implement' is asked with 'head {input.prompt}', which is not one read of a file-kind input; it is {input.<name>} whole, the name of an input the workflow 'flow/pipeline' declares of the kind 'file'"],
            WorkflowValidator.Validate(Resolved("""
                name: flow
                steps:
                  - name: items
                    use: ./pipeline/pipeline.yml
                    with:
                      title: t
                      prompt: prompts/implement.md
                """, Swap(Pipeline, "    prompt: prompts/implement.md\n", "    prompt: 'head {input.prompt}'\n")), hasCommitTemplate: false));

        Assert.Equal(
            ["step 'items.implement' is asked with '{input.title}', which is not one read of a file-kind input; it is {input.<name>} whole, the name of an input the workflow 'flow/pipeline' declares of the kind 'file'"],
            WorkflowValidator.Validate(Resolved("""
                name: flow
                steps:
                  - name: items
                    use: ./pipeline/pipeline.yml
                    with:
                      title: t
                      prompt: prompts/implement.md
                """, Swap(Pipeline, "    prompt: prompts/implement.md\n", "    prompt: '{input.title}'\n")), hasCommitTemplate: false));

        Assert.Equal(
            ["step 'items.implement' is asked with '{input.prompt}', and 'items' fills the input with no path; a step is asked with a file, and the use fills it with one"],
            WorkflowValidator.Validate(Resolved("""
                name: flow
                steps:
                  - name: items
                    use: ./pipeline/pipeline.yml
                    with:
                      title: t
                """, gated), hasCommitTemplate: false));
    }

    /// <summary>
    /// The file a use fills a prompt input with is the file its step is asked with, where it runs: each use asks with
    /// its own, the placeholders of the file filled from the scope the step runs in.
    /// </summary>
    [Fact]
    public async Task AStepAskedWithAFileItsUseFills_IsAskedWithThatFile()
    {
        var gated = Swap(Pipeline, "    prompt: prompts/implement.md\n", "    prompt: '{input.prompt}'\n");
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(
            Resolved("""
                name: flow
                steps:
                  - name: items
                    use: ./pipeline/pipeline.yml
                    with:
                      title: the work
                      prompt: prompts/one.md
                """, gated),
            runner,
            Facts(),
            new JumpingClock(),
            workflowFile: (owner, named) => named == "prompts/one.md" ? "Build {input.title} now." : throw new ArgumentException($"{owner ?? "flow"}/{named}"));

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Build the work now.", Prompt(runner, "items.implement"));

        return;

        static string Prompt(FakeRunner runner, string instance)
            => string.Concat(runner.Asked[instance]["prompt"].Select(piece => piece.Text));
    }

    /// <summary>
    /// WF2: a step held off by its own workflow's gate - a <c>when</c> that reads one input of it alone - runs where a
    /// use opens the gate and never where one closes it: the alternatives a pipeline declares are the use's to hold
    /// ready or off, and a closed gate is the one way the same pipeline runs with fewer steps.
    /// </summary>
    [Fact]
    public async Task AStepHeldOffByItsWorkflowsGate_RunsOnlyWhereTheUseOpensIt()
    {
        var pipeline = Swap(
            Swap(Pipeline,
                "  - name: prompt\n    kind: file\n",
                "  - name: prompt\n    kind: file\n  - name: alternatives\n    kind: value\n    required: true\n"),
            "steps:\n  - name: implement\n",
            "steps:\n  - name: spare\n    type: orient\n    when: 'input.alternatives'\n  - name: implement\n");

        var open = new FakeRunner();
        _ = await new FlowCoordinator(Resolved("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
                  alternatives: 'true'
            """, pipeline), open, Facts(), new JumpingClock()).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["items.spare", "items.implement", "items.verify"], open.Started.Where(started => started.StartsWith("items.", StringComparison.Ordinal)));

        var closed = new FakeRunner();
        var shut = new FlowCoordinator(Resolved("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
                  alternatives: 'false'
            """, pipeline), closed, Facts(), new JumpingClock());
        _ = await shut.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["items.implement", "items.verify"], closed.Started.Where(started => started.StartsWith("items.", StringComparison.Ordinal)));
        Assert.Equal(FlowInstanceState.Skipped, shut.Instances["items.spare"].State);
    }

    /// <summary>
    /// The value a use fills a step-kind input with is held to what the policy it reads is: one filled
    /// <c>recover</c>, on a step of the recovery, calls the recovery from inside itself and is refused.
    /// </summary>
    [Fact]
    public void AFailToFilledWithRecover_InTheRecoveryIsRefused()
    {
        var pipeline = Swap(Pipeline, "    onFailure: recover\n", "    onFailure: '{input.failTo}'\n");

        Assert.Equal(
            ["step 'recover.items.verify', which reads its onFailure from '{input.failTo}', filled with 'recover' by 'recover.items', is a step of the recovery and gives its failure to the recovery, and so calls itself; a step never does - one that is to run again after it failed says retries"],
            WorkflowValidator.Validate(Resolved("""
                name: flow
                steps:
                  - name: head
                    type: orient
                    onFailure: recover
                recover:
                    onSuccess: failed-step
                    onFailure: pause
                    steps:
                      - name: items
                        use: ./pipeline/pipeline.yml
                        with:
                          title: t
                          prompt: prompts/implement.md
                          failTo: recover
                """, pipeline), hasCommitTemplate: false));
    }

    /// <summary>
    /// WF4, the key: a use that says <c>useParentLimits: false</c> governs itself - the workflow it uses declares
    /// run-level keys of its own, carried whole and said where it declares one - while one that says nothing (or true)
    /// applies none of them, said as not applied. A step that is no use saying the key is refused, and a use that
    /// governs itself over a workflow declaring an amend is refused too, for an amend is a run's own.
    /// </summary>
    [Fact]
    public void AUseThatGovernsItself_CarriesTheWorkflowsOwnSettings_AndAnAmendIsRefused()
    {
        var withAmend = Swap(Pipeline, "name: pipeline\n", """
            name: pipeline
            budget:
              usd: 5
            maxReasoningAgents: 3
            maxRetries: 1
            answers:
              loopBound:
                answer: accept
            amend:
              step: plan
              scope: 'update.pieces'
              whenAddScope: round.items
              whenChangeScope: round.items
              whenRemoveScope: round.items

            """ + "\n");

        // Without the key: everything it declares is said as not applied, and none of it is carried.
        var plain = Resolved(Caller(), withAmend);
        var use = plain.Steps.Single(step => step.Name == "items").Use!;

        Assert.Equal(["amend", "answers", "budget", "limits"], [.. use.NotApplied.Order(StringComparer.Ordinal)]);
        Assert.Null(use.Own);

        // With it: carried whole, said where declared, and nothing said as not applied.
        var governed = Resolved(Caller(useParentLimits: "false"), withAmend);
        var own = governed.Steps.Single(step => step.Name == "items").Use!.Own;

        Assert.NotNull(own);
        Assert.Empty(governed.Steps.Single(step => step.Name == "items").Use!.NotApplied);
        Assert.Equal((5d, 3, 1), (own!.Budget!.Usd, own.MaxReasoningAgents, own.MaxRetries));
        Assert.Equal("accept", own.Answers!["loopBound"].Answer);
        Assert.True(own.Amends);
        Assert.Equal(["budget", "limits", "answers"], own.Declared);

        // And the amend it declares is refused, never silently dropped.
        Assert.Equal(
            ["step 'items' says useParentLimits: false on the workflow 'flow/pipeline' it uses, which declares an amend; an amend is a run's own - a request amended, or an answer changed, while it works - and no step of a run applies one"],
            WorkflowValidator.Validate(governed, hasCommitTemplate: false));

        // A step that is no use saying the key is refused where the file is read.
        Assert.Contains(ParserProblems("""
                name: flow
                steps:
                  - name: plain
                    type: orient
                    useParentLimits: false
                """), problem => problem.Contains("says useParentLimits and names no workflow to use", StringComparison.Ordinal));

        return;

        static string Caller(string useParentLimits = "")
            => $$"""
                name: flow
                steps:
                  - name: items
                    use: ./pipeline/pipeline.yml{{(useParentLimits.Length == 0 ? string.Empty : $"\n    useParentLimits: {useParentLimits}")}}
                    with:
                      title: t
                      prompt: prompts/implement.md
                """;
    }

    /// <summary>
    /// WF4: a step of a use that governs itself falls back to the workflow it runs in where it says neither of its own -
    /// its <c>maxRetries</c> is what its incomplete endings are retried to, beside the run's own.
    /// </summary>
    [Fact]
    public async Task TheStepsOfAUseThatGovernsItself_FallBackToItsRetries()
    {
        var pipeline = Swap(Pipeline, "steps:\n  - name: implement\n", "steps:\n  - name: flaky\n    type: orient\n  - name: implement\n");

        var governed = new FakeRunner().On("flaky", () => FlowStepResult.Ended(passed: false, HarnessExit.Incomplete, "still incomplete"));
        var run = new FlowCoordinator(
            Resolved($"""
                name: flow
                maxRetries: 3
                steps:
                  - name: items
                    use: ./pipeline/pipeline.yml
                    useParentLimits: false
                    with:
                      title: t
                      prompt: prompts/implement.md
                """, Swap(pipeline, "name: pipeline\n", "name: pipeline\nmaxRetries: 1\n")),
            governed,
            Facts(),
            new JumpingClock());
        _ = await run.RunAsync(TestContext.Current.CancellationToken);

        // The pipeline's maxRetries (1) caps it: two attempts, not the run's four.
        Assert.Equal(2, governed.Started.Count(started => started == "items.flaky"));
        Assert.Equal(FlowInstanceState.Failed, run.Instances["items.flaky"].State);

        var plain = new FakeRunner().On("flaky", () => FlowStepResult.Ended(passed: false, HarnessExit.Incomplete, "still incomplete"));
        var ungoverned = new FlowCoordinator(
            Resolved($"""
                name: flow
                maxRetries: 3
                steps:
                  - name: items
                    use: ./pipeline/pipeline.yml
                    with:
                      title: t
                      prompt: prompts/implement.md
                """, Swap(pipeline, "name: pipeline\n", "name: pipeline\nmaxRetries: 1\n")),
            plain,
            Facts(),
            new JumpingClock());
        _ = await ungoverned.RunAsync(TestContext.Current.CancellationToken);

        // Without the key, the pipeline's maxRetries is not applied: the run's (3) holds, four attempts.
        Assert.Equal(4, plain.Started.Count(started => started == "items.flaky"));
    }

    /// <summary>
    /// WF4: the limits of a use that governs itself gate its steps alone, counted across every instance of the use, and
    /// its steps are counted against the caller's gates no more - a step of the caller's runs beside the use's own,
    /// held to the run's own gate.
    /// </summary>
    [Fact]
    public async Task TheStepsOfAUseThatGovernsItself_CountAgainstTheirOwnGateAlone()
    {
        var pipeline = Swap(Pipeline, "steps:\n  - name: implement\n", "steps:\n  - name: first\n    type: reason\n    agent: worker\n    prompt: prompts/implement.md\n    outputs: schemas/implement.json\n  - name: second\n    type: reason\n    agent: worker\n    prompt: prompts/implement.md\n    outputs: schemas/implement.json\n  - name: implement\n");

        var caller = $"""
            name: flow
            maxReasoningAgents: 2
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                useParentLimits: false
                with:
                  title: t
                  prompt: prompts/implement.md
            """;

        var governed = new FakeRunner();
        var gate = governed.Hold("first");
        var run = new FlowCoordinator(
            Resolved(caller, Swap(pipeline, "name: pipeline\n", "name: pipeline\nmaxReasoningAgents: 1\n")),
            governed,
            Facts(),
            new JumpingClock());
        var going = run.RunAsync(TestContext.Current.CancellationToken);

        // A run that ends without starting the held step fails here rather than waiting on a start that cannot come.
        Assert.Same(gate.StartedAsync(), await Task.WhenAny(gate.StartedAsync(), going));

        // The use's gate (1) holds its second step; the run's (2) is not the one that governs it, for where the use's
        // steps are counted against their own gate alone, the run's own steps still have the run's.
        Assert.DoesNotContain("items.second", governed.Started);

        gate.Open(passed: true);
        _ = await going;

        Assert.Equal(["items.first", "items.second"], [.. governed.Started.Where(started => started.Split('.')[^1] is "first" or "second")]);
    }

    /// <summary>
    /// WF4: a question a step of a use that governs itself raises is answered by the workflow it runs in before the
    /// run's own answers - and where no use governs, the workflow's answers are never asked at all.
    /// </summary>
    [Fact]
    public async Task AQuestionOfAStepOfAUseThatGovernsItself_IsAnsweredByItsAnswersFirst()
    {
        var pipeline = Swap(Pipeline, "steps:\n  - name: implement\n", "steps:\n  - name: spin\n    repeat:\n      until: 'false'\n      maxIterations: 2\n    steps:\n      - name: once\n        type: orient\n  - name: implement\n");
        pipeline = Swap(pipeline, "name: pipeline\n", """
            name: pipeline
            answers:
              loopBound:
                answer: accept

            """ + "\n");

        string Caller(bool governs)
            => $$"""
                name: flow
                steps:
                  - name: items
                    use: ./pipeline/pipeline.yml{{(governs ? "\n    useParentLimits: false" : string.Empty)}}
                    with:
                      title: t
                      prompt: prompts/implement.md
                """;

        var governed = new FakeRunner();
        var run = new FlowCoordinator(Resolved(Caller(governs: true), pipeline), governed, Facts(), new JumpingClock());
        _ = await run.RunAsync(TestContext.Current.CancellationToken);

        // The workflow's accept ended the loop at its bound, before the run's abort was ever asked.
        Assert.Equal(FlowInstanceState.Done, run.Instances["items"].State);
        Assert.Equal(FlowInstanceState.Done, run.Instances["items.spin"].State);

        var plain = new FakeRunner();
        var ungovernedDefinition = Resolved(Caller(governs: false), pipeline);
        var ungoverned = new FlowCoordinator(ungovernedDefinition, plain, Facts(), new JumpingClock());
        _ = await ungoverned.RunAsync(TestContext.Current.CancellationToken);

        // Without the key, the workflow's answers are not applied: the question is a person's.
        Assert.Equal(FlowInstanceState.Paused, ungoverned.Instances["items.spin"].State);
        Assert.Contains(ungoverned.Questions, question => question.Kind == "loopBound" && question.State == "open");
    }

    /// <summary>
    /// WF4b: a question a step of a use that governs itself raises is recommended by the advisor the workflow it runs in
    /// declares - its files read in its own directory - and where it declares none, the run's own advisor stands as it
    /// always did.
    /// </summary>
    [Fact]
    public async Task TheQuestionsOfAStepOfAUseThatGovernsItself_AreRecommendedByItsAdvisor()
    {
        var pipeline = Swap(Pipeline, "steps:\n  - name: implement\n", "steps:\n  - name: spin\n    repeat:\n      until: 'false'\n      maxIterations: 2\n    steps:\n      - name: once\n        type: orient\n  - name: implement\n");
        pipeline = Swap(pipeline, "name: pipeline\n", """
            name: pipeline
            advisor:
              agent: consul
              prompt: prompts/advise.md

            """ + "\n");

        string Caller(bool governs)
            => $$"""
                name: flow
                advisor:
                  agent: helper
                  prompt: prompts/advise.md
                steps:
                  - name: items
                    use: ./pipeline/pipeline.yml{{(governs ? "\n    useParentLimits: false" : string.Empty)}}
                    with:
                      title: t
                      prompt: prompts/implement.md
                """;

        var governed = new FakeAdvisor();
        var governedRun = new FlowCoordinator(
            Resolved(Caller(governs: true), pipeline),
            new FakeRunner(),
            Facts(),
            new JumpingClock(),
            advisor: governed,
            workflowFileIfThere: _ => "the bar");
        _ = await governedRun.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("consul", "flow/pipeline"), governed.Called.Single());

        var plain = new FakeAdvisor();
        var plainRun = new FlowCoordinator(
            Resolved(Caller(governs: false), pipeline),
            new FakeRunner(),
            Facts(),
            new JumpingClock(),
            advisor: plain,
            workflowFileIfThere: _ => "the bar");
        _ = await plainRun.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("helper", "flow"), plain.Called.Single());
    }

    /// <summary>
    /// WF4b: the run's one graph takes the shape a use that governs itself declares where the run's own workflow says
    /// none - its reason steps' serving and its graphify steps fall back to it - and the run's own shape stands wherever
    /// it declares one.
    /// </summary>
    [Fact]
    public async Task TheGraphOfAStepOfAUseThatGovernsItself_FallsBackToTheShapeItDeclares()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig());
        var context = await harness.ContextLoader.LoadAsync(temp.Path, token);
        var place = new FlowRunPlace(temp.Path, "r1", "0123456789abcdef0123456789abcdef01234567", WorkflowFileParser.Parse("workflows/flow/flow.yml", "name: flow\nsteps:\n  - name: one\n    type: orient\n"), null);
        var governing = new FlowCodebaseGraph(Include: ["src/**"]);

        Assert.Equal(["src/**"], [.. FlowGraphSyncs.RequestOf(context, place, "why", governing: governing).Shape.Include!.Select(pattern => pattern.ToString())]);
        Assert.Empty(FlowGraphSyncs.RequestOf(context, place, "why").Shape.Include ?? []);

        var own = place with { Definition = place.Definition with { CodebaseGraph = new FlowCodebaseGraph(Include: ["tests/**"]) } };
        Assert.Equal(["tests/**"], [.. FlowGraphSyncs.RequestOf(context, own, "why", governing: governing).Shape.Include!.Select(pattern => pattern.ToString())]);
    }

    /// <summary>
    /// WF4: a use that governs itself caps the instances its steps may open, beside the run's own cap, counted across
    /// every instance of the use - a group of it past the cap is refused naming the workflow and its maxInstances.
    /// </summary>
    [Fact]
    public async Task AUseThatGovernsItself_CapsItsOwnInstances()
    {
        var pipeline = Swap(Pipeline, "  - name: item\n    kind: value\n", "  - name: item\n    kind: value\n  - name: many\n    kind: value\n    required: true\n");
        pipeline = Swap(pipeline, "steps:\n  - name: implement\n", "steps:\n  - name: batch\n    forEach: 'input.many'\n    steps:\n      - name: one\n        type: orient\n  - name: implement\n");
        pipeline = Swap(pipeline, "name: pipeline\n", "name: pipeline\nmaxInstances: 4\n");

        var runner = new FakeRunner();
        var run = new FlowCoordinator(Resolved($"""
            name: flow
            steps:
              - name: list
                type: orient
              - name: items
                use: ./pipeline/pipeline.yml
                useParentLimits: false
                dependsOn: [list]
                with:
                  title: t
                  prompt: prompts/implement.md
                  many: 'list.items'
            """, pipeline), runner.On("list", () => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new System.Text.Json.Nodes.JsonObject { ["items"] = new System.Text.Json.Nodes.JsonArray([1, 2, 3]) })), Facts(), new JumpingClock());
        _ = await run.RunAsync(TestContext.Current.CancellationToken);


        Assert.Equal(FlowInstanceState.Failed, run.Instances["items"].State);
        Assert.Contains(
            "would bring the workflow 'flow/pipeline' to 7 step instances, and at most 4 are allowed (its maxInstances, beside the run's own cap)",
            run.Instances["items.batch"].Error,
            StringComparison.Ordinal);
    }

    /// <summary>The facts a run of these definitions needs, with nothing configured.</summary>
    private static ConfiguredRunnerFacts Facts()
        => new(new HarnessConfig());
    /// <summary>The parser's problems of a whole definition, whole: these tests read one that does not.</summary>
    private static IReadOnlyList<string> ParserProblems(string text)
    {
        try
        {
            _ = WorkflowFileParser.Parse("workflows/flow/flow.yml", text);
        }
        catch (HarnessException refused)
        {
            return [.. refused.Message.Split('\n').Select(line => line.Trim(' ', '-', '\r')).Where(line => !line.EndsWith(":", StringComparison.Ordinal) && line.Length > 0)];
        }

        return [];
    }

    /// <summary>A runner that answers each step as its test scripted it.</summary>
    private sealed class FakeRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepResult>> _scripts = new(StringComparer.Ordinal);

        /// <summary>Every instance asked, in the order the coordinator started it.</summary>
        public List<string> Started { get; } = [];

        /// <summary>Each reason step's asked-for files, by its instance: the prompt and resume prompt, in their pieces.</summary>
        public Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<FlowTemplate.Piece>>> Asked { get; } = new(StringComparer.Ordinal);

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public FakeRunner On(string step, FlowStepResult result)
        {
            _scripts[step] = () => result;
            return this;
        }

        /// <summary>Answers <paramref name="step"/> by its script, which sees nothing but itself and answers at once.</summary>
        public FakeRunner On(string step, Func<FlowStepResult> script)
        {
            _scripts[step] = script;
            return this;
        }

        /// <summary>
        /// Holds every run of <paramref name="step"/> open until the hold is opened, for a test that watches what
        /// starts beside it.
        /// </summary>
        public HeldStep Hold(string step)
        {
            _holds[step] = new HeldStep();
            return _holds[step];
        }

        private readonly Dictionary<string, HeldStep> _holds = new(StringComparer.Ordinal);

        /// <summary>One held-open step, released by the test that held it.</summary>
        public sealed class HeldStep
        {
            private readonly TaskCompletionSource _began = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<FlowStepResult> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Completes when the held step has started.</summary>
            public Task StartedAsync() => _began.Task;

            /// <summary>Ends the held step, passed or failed as asked.</summary>
            public void Open(bool passed) => _ended.SetResult(FlowStepResult.Ended(passed, HarnessExit.Success));

            /// <summary>Says the held step began.</summary>
            public void Begun() => _began.SetResult();

            /// <summary>The step's run, held until <see cref="Open"/> opens it.</summary>
            public Task<FlowStepResult> EndedAsync() => _ended.Task;
        }

        /// <inheritdoc/>
        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            Started.Add(run.Instance);

            if (run.Prompts is { } asked)
            {
                Asked[run.Instance] = asked;
            }

            if (_holds.TryGetValue(run.Step.Name, out var held))
            {
                held.Begun();
                return held.EndedAsync();
            }

            return Task.FromResult(_scripts.GetValueOrDefault(run.Step.Name, () => FlowStepResult.Ended(passed: true, HarnessExit.Success))());
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>An advisor runner that records each asking's advisor and workflow and obtains nothing.</summary>
    private sealed class FakeAdvisor : IFlowAdvisorRunner
    {
        /// <summary>Each asking's advisor agent and the workflow its files are read in, in the order they were asked.</summary>
        public List<(string Agent, string Workflow)> Called { get; } = [];

        /// <inheritdoc/>
        public Task<FlowAdvice> AdviseAsync(FlowAdvisorCall call, CancellationToken cancellationToken)
        {
            Called.Add((call.Advisor.Agent, call.Workflow));

            return Task.FromResult(new FlowAdvice(Answer: null, Why: null, Text: null, NotObtained: "the test sought none"));
        }
    }
}
