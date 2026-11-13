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
    /// WF4: the reasoning gate of a use that governs itself and declares one gates its steps alone, and its steps are
    /// counted against the caller's gate no more: the use's second step waits for its first though the run's gate (2)
    /// has room, and a step of the caller's starts beside the use's first, for the use's step holds none of the run's
    /// slots. Read from the run's own order of attempts begun and instances ended, never from what a runner saw when.
    /// </summary>
    [Fact]
    public async Task TheStepsOfAUseThatGovernsItself_CountAgainstTheirOwnGateAlone()
    {
        var run = new FlowCoordinator(
            Resolved(GatedCaller(runGate: 2), Swap(TwoReasoners, "name: pipeline\n", "name: pipeline\nmaxReasoningAgents: 1\n")),
            new FakeRunner(),
            Facts(),
            new JumpingClock());
        _ = await run.RunAsync(TestContext.Current.CancellationToken);

        // The use's gate (1): no two of its reasoning steps at once - the run's gate of 2 never governed them.
        Assert.Empty(Overlapping(run, ["items.first", "items.second", "items.implement"]));

        // The run's gate counts the caller's step alone: it began with the use's first, in the same admission.
        Assert.Equal([("items.first", "beside")], Overlapping(run, ["items.first", "beside"]));
    }

    /// <summary>
    /// WF4: a use that governs itself and declares no reasoning gate of its own counts its reasoning steps against the
    /// run's gate, with the caller's own - a use never widens the run's limits by declaring nothing: under the run's gate
    /// of 1, no two reasoning steps of the run, the use's or the caller's, ever run at once.
    /// </summary>
    [Fact]
    public async Task TheStepsOfAUseThatDeclaresNoGate_CountAgainstTheRunsGate()
    {
        var run = new FlowCoordinator(
            Resolved(GatedCaller(runGate: 1), Swap(TwoReasoners, "name: pipeline\n", "name: pipeline\nmaxRetries: 1\n")),
            new FakeRunner(),
            Facts(),
            new JumpingClock());
        _ = await run.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Done, run.Instances["beside"].State);
        Assert.Empty(Overlapping(run, ["items.first", "items.second", "items.implement", "beside"]));
    }

    /// <summary>
    /// WF4: the gate of a use that governs itself is counted across every instance of the use - each work item of a
    /// forEach that uses it is one, and the use's gate of 1 runs their reasoning steps one at a time, under a run's gate
    /// of 4 that would have let them all start at once.
    /// </summary>
    [Fact]
    public async Task TheGateOfAUseThatGovernsItself_IsCountedAcrossEveryItemOfItsForEach()
    {
        var runner = new FakeRunner().On("listing", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["list"] = new JsonArray(1, 2, 3) }));
        var run = new FlowCoordinator(
            Resolved("""
                name: flow
                maxReasoningAgents: 4
                steps:
                  - name: listing
                    type: orient
                  - name: items
                    use: ./pipeline/pipeline.yml
                    useParentLimits: false
                    forEach: listing.list
                    dependsOn: [listing]
                    with:
                      item: item
                      title: t
                      prompt: prompts/implement.md
                """, Swap(Pipeline, "name: pipeline\n", "name: pipeline\nmaxReasoningAgents: 1\n")),
            runner,
            Facts(),
            new JumpingClock());
        _ = await run.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Done, run.Instances["items"].State);
        Assert.Empty(Overlapping(run, ["items[0].implement", "items[1].implement", "items[2].implement"]));
    }

    /// <summary>The pipeline with two reasoning steps of its own before its implement step, for the tests of its gates.</summary>
    private static readonly string TwoReasoners = Swap(Pipeline, "steps:\n  - name: implement\n", "steps:\n  - name: first\n    type: reason\n    agent: worker\n    prompt: prompts/implement.md\n    outputs: schemas/implement.json\n  - name: second\n    type: reason\n    agent: worker\n    prompt: prompts/implement.md\n    outputs: schemas/implement.json\n  - name: implement\n");

    /// <summary>A caller that uses the pipeline governing itself, with a reasoning step of its own declared after it, under the run's reasoning gate.</summary>
    private static string GatedCaller(int runGate)
        => $"""
            name: flow
            maxReasoningAgents: {runGate}
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                useParentLimits: false
                with:
                  title: t
                  prompt: prompts/implement.md
              - name: beside
                type: reason
                agent: worker
                prompt: prompts/implement.md
                outputs: schemas/implement.json
            """;

    /// <summary>
    /// Each two of <paramref name="instances"/> that ran at once, in the order given: one began before the other ended,
    /// by the run's own order of attempts begun and instances ended - which no runner's thread changes.
    /// </summary>
    private static List<(string, string)> Overlapping(FlowCoordinator run, IReadOnlyList<string> instances)
    {
        var overlapping = new List<(string, string)>();

        for (var one = 0; one < instances.Count; one++)
        {
            for (var other = one + 1; other < instances.Count; other++)
            {
                var (a, b) = (run.Instances[instances[one]], run.Instances[instances[other]]);
                Assert.NotEqual(0, a.EndedIn);
                Assert.NotEqual(0, b.EndedIn);

                if (a.AttemptBegan < b.EndedIn && b.AttemptBegan < a.EndedIn)
                {
                    overlapping.Add((instances[one], instances[other]));
                }
            }
        }

        return overlapping;
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
    /// declares - its files read in its own directory, its prompt handed the inputs the use filled and the bar the
    /// workflow's own <c>texts.recommendations</c> names, and what it recommends credited to it - and where it declares
    /// none, the run's own advisor stands as it always did, handed the run's inputs and bar.
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
            texts:
              recommendations: texts/bar.md

            """ + "\n");

        string Caller(bool governs)
            => $$"""
                name: flow
                advisor:
                  agent: helper
                  prompt: prompts/advise.md
                inputs:
                  - name: title
                    kind: text
                steps:
                  - name: items
                    use: ./pipeline/pipeline.yml{{(governs ? "\n    useParentLimits: false" : string.Empty)}}
                    with:
                      title: t
                      prompt: prompts/implement.md
                """;

        var governed = new FakeAdvisor(answer: "accept");
        var governedRun = new FlowCoordinator(
            Resolved(Caller(governs: true), pipeline),
            new FakeRunner(),
            Facts(),
            new JumpingClock(),
            workflowFile: (owner, file) => $"{owner ?? "the run"}: {file}",
            advisor: governed,
            workflowFileIfThere: _ => "the bar");
        _ = await governedRun.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("consul", "flow/pipeline"), governed.Called.Single());
        var call = governed.Calls.Single();
        Assert.Equal("flow/pipeline: texts/bar.md", call.Bar);
        Assert.Equal("t", call.Inputs["title"]?.GetValue<string>());
        Assert.Equal(("given", "consul"), (governedRun.Questions.Single().Recommendation?.State, governedRun.Questions.Single().Recommendation?.Agent));

        var plain = new FakeAdvisor(answer: "accept");
        var plainRun = new FlowCoordinator(
            Resolved(Caller(governs: false), pipeline),
            new FakeRunner(),
            Facts(),
            new JumpingClock(),
            workflowFile: (owner, file) => $"{owner ?? "the run"}: {file}",
            advisor: plain,
            workflowFileIfThere: _ => "the bar",
            inputs: new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "the run's own" });
        _ = await plainRun.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("helper", "flow"), plain.Called.Single());
        Assert.Equal("the bar", plain.Calls.Single().Bar);
        Assert.Equal("the run's own", plain.Calls.Single().Inputs["title"]?.GetValue<string>());
        Assert.Equal(("given", "helper"), (plainRun.Questions.Single().Recommendation?.State, plainRun.Questions.Single().Recommendation?.Agent));
    }

    /// <summary>
    /// A question of a step of a use that governs itself is answered in one order, nearest first: the step's own answers,
    /// the groups around it within the used workflow, the used workflow's own answers, and then the caller's groups -
    /// the use step's own key among them - and the run's. A group within the workflow answers before the workflow, and
    /// the workflow before the use step that calls it.
    /// </summary>
    [Fact]
    public async Task AQuestionOfAStepOfAUseThatGovernsItself_IsAnsweredNearestFirst()
    {
        async Task<string?> AnsweredWhereAsync(bool groupAnswers, bool workflowAnswers)
        {
            var hold = groupAnswers ? "  - name: hold\n    answers:\n      loopBound: accept\n" : "  - name: hold\n";
            var pipeline = Swap(Pipeline, "steps:\n  - name: implement\n", $"steps:\n{hold}    steps:\n      - name: spin\n        repeat:\n          until: 'false'\n          maxIterations: 1\n        steps:\n          - name: once\n            type: orient\n  - name: implement\n");
            pipeline = workflowAnswers ? Swap(pipeline, "name: pipeline\n", "name: pipeline\nanswers:\n  loopBound: accept\n") : pipeline;
            var run = new FlowCoordinator(Resolved("""
                name: flow
                answers:
                  loopBound: accept
                steps:
                  - name: around
                    answers:
                      loopBound: accept
                    steps:
                      - name: items
                        use: ./pipeline/pipeline.yml
                        useParentLimits: false
                        answers:
                          loopBound: accept
                        with:
                          title: t
                          prompt: prompts/implement.md
                """, pipeline), new FakeRunner(), Facts(), new JumpingClock());
            _ = await run.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowInstanceState.Done, run.Instances["around.items.hold.spin"].State);
            return Assert.Single(Assert.Single(run.Questions).Given).Where;
        }

        // A group within the used workflow answers first, then the workflow's own answers, then the use step's own key,
        // the caller's groups around it and the run's.
        Assert.Equal("group 'hold'", await AnsweredWhereAsync(groupAnswers: true, workflowAnswers: true));
        Assert.Equal("the workflow 'flow/pipeline'", await AnsweredWhereAsync(groupAnswers: false, workflowAnswers: true));
        Assert.Equal("group 'items'", await AnsweredWhereAsync(groupAnswers: false, workflowAnswers: false));
    }

    /// <summary>
    /// A text a step reads by its name at run time is the nearest used workflow's that declares the name, and else the
    /// run's: a step of the caller reads the run's text, a step of the used workflow reads its own workflow's text of
    /// the same name, and a name only the run declares reaches the used workflow's steps - each read live, from the
    /// directory of the workflow that declares it.
    /// </summary>
    [Fact]
    public async Task ATextReadAtRunTime_IsTheUsedWorkflowsOwnFirst_AndElseTheRuns()
    {
        var pipeline = Swap(Pipeline, "name: pipeline\n", "name: pipeline\ntexts:\n  closure: texts/closure.md\n");
        pipeline = Swap(pipeline, "    outputs: schemas/implement.json\n", "    outputs: schemas/implement.json\n    when: 'text.closure == \"the pipeline''s closure\" && text.shared == \"the run''s shared\"'\n");
        var files = new Dictionary<(string?, string), string>
        {
            [(null, "texts/closure.md")] = "the run's closure",
            [(null, "texts/shared.md")] = "the run's shared",
            [("flow/pipeline", "texts/closure.md")] = "the pipeline's closure",
        };
        var definition = Resolved("""
            name: flow
            texts:
              closure: texts/closure.md
              shared: texts/shared.md
            steps:
              - name: first
                type: orient
                when: 'text.closure == "the run''s closure"'
              - name: items
                use: ./pipeline/pipeline.yml
                dependsOn: [first]
                with:
                  title: t
                  prompt: prompts/implement.md
            """, pipeline);
        var coordinator = new FlowCoordinator(
            definition,
            new FakeRunner(),
            Facts(),
            new JumpingClock(),
            workflowFile: (owner, file) => files.GetValueOrDefault((owner, file), $"the file {file}"));

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal((FlowInstanceState.Done, (string?)null), (coordinator.Instances["first"].State, coordinator.Instances["first"].Error));
        Assert.Equal((FlowInstanceState.Done, (string?)null), (coordinator.Instances["items.implement"].State, coordinator.Instances["items.implement"].Error));

        // A run that reads no workflow files reads no text, and says so: never a text read as nothing.
        var unread = new FlowCoordinator(definition, new FakeRunner(), Facts(), new JumpingClock());
        _ = await unread.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Failed, unread.Instances["first"].State);
        Assert.Contains("the text 'closure' is not read: this run reads no workflow files", unread.Instances["first"].Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// WF4b: the run's one graph has one shape, settled once for the run - the run's own workflow's where it declares one,
    /// else the shape a use that governs itself declares - and every request of it is made under that shape, whoever
    /// asks: a sync at a step's start or end, a step's serving, a graphify step. One graph synced under two shapes would
    /// list files and drop them again between its syncs.
    /// </summary>
    [Fact]
    public async Task TheRunsOneGraph_TakesTheShapeAGoverningUseDeclares_WhereTheRunDeclaresNone()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig());
        var context = await harness.ContextLoader.LoadAsync(temp.Path, token);
        var pipeline = Swap(Pipeline, "name: pipeline\n", "name: pipeline\ncodebaseGraph:\n  include: ['src/**']\n");

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

        FlowRunPlace PlaceOf(WorkflowDefinition definition)
            => new(temp.Path, "r1", "0123456789abcdef0123456789abcdef01234567", definition, null);

        static IReadOnlyList<string> Included(DssHarness.Core.Codebase.CodebaseGraphSync request)
            => [.. (request.Shape.Include ?? []).Select(pattern => pattern.ToString())];

        var governed = PlaceOf(Resolved(Caller(governs: true), pipeline));

        // Every request of the run's graph, whatever asks it, is made under the use's shape.
        Assert.Equal(["src/**"], Included(FlowGraphSyncs.RequestOf(context, governed, "items.implement:start")));
        Assert.Equal(["src/**"], Included(FlowGraphSyncs.RequestOf(context, governed, "items.implement:graph", rebuild: true)));

        // The run's own shape stands where it declares one, and a use that does not govern itself shapes nothing.
        var own = governed with { Definition = governed.Definition with { CodebaseGraph = new FlowCodebaseGraph(Include: ["tests/**"]) } };
        Assert.Equal(["tests/**"], Included(FlowGraphSyncs.RequestOf(context, own, "why")));
        Assert.Empty(Included(FlowGraphSyncs.RequestOf(context, PlaceOf(Resolved(Caller(governs: false), pipeline)), "why")));
    }

    /// <summary>
    /// WF4: a use that governs itself and declares maxInstances caps the instances its steps open, counted across every
    /// instance of the use and in the place of the run's own cap - a group of it past its cap is refused naming the
    /// workflow, and one within its cap opens though the run's cap alone would have refused it - while a use that
    /// declares no cap leaves its steps counted against the run's, for a use never widens the run's limits by declaring
    /// nothing.
    /// </summary>
    [Fact]
    public async Task AUseThatGovernsItself_CapsItsOwnInstances_InThePlaceOfTheRunsCap()
    {
        var pipeline = Swap(Pipeline, "  - name: item\n    kind: value\n", "  - name: item\n    kind: value\n  - name: many\n    kind: value\n    required: true\n");
        pipeline = Swap(pipeline, "steps:\n  - name: implement\n", "steps:\n  - name: batch\n    forEach: 'input.many'\n    steps:\n      - name: one\n        type: orient\n  - name: implement\n");

        async Task<FlowCoordinator> RunAsync(int? runCap, string pipelineText)
        {
            var runner = new FakeRunner().On("list", () => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["items"] = new JsonArray([1, 2, 3]) }));
            var run = new FlowCoordinator(Resolved($"""
                name: flow{(runCap is { } cap ? $"\nmaxInstances: {cap}" : string.Empty)}
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
                """, pipelineText), runner, Facts(), new JumpingClock());
            _ = await run.RunAsync(TestContext.Current.CancellationToken);
            return run;
        }

        // The use's own cap (4): items.batch itself, then three items and three members of them, are the use's seven.
        var capped = await RunAsync(runCap: null, Swap(pipeline, "name: pipeline\n", "name: pipeline\nmaxInstances: 4\n"));
        Assert.Equal(FlowInstanceState.Failed, capped.Instances["items"].State);
        Assert.Equal(
            "group 'items.batch' would bring the workflow 'flow/pipeline' to 7 step instances, and at most 4 are allowed (its maxInstances, counted across every instance of the use in the place of the run's own cap). Raise the workflow's maxInstances, or lower a loop's bounds",
            capped.Instances["items.batch"].Error);

        // Within the use's cap (20) the run's own (5) counts only list and items: the use's eleven open and run.
        var within = await RunAsync(runCap: 5, Swap(pipeline, "name: pipeline\n", "name: pipeline\nmaxInstances: 20\n"));
        Assert.Equal(FlowInstanceState.Done, within.Instances["items"].State);
        Assert.Equal(FlowInstanceState.Done, within.Instances["items.batch"].State);

        // A use that declares no cap is counted against the run's: list, items and items.batch, then six more, pass 5.
        var uncapped = await RunAsync(runCap: 5, Swap(pipeline, "name: pipeline\n", "name: pipeline\nmaxRetries: 1\n"));
        Assert.Equal(FlowInstanceState.Failed, uncapped.Instances["items"].State);
        Assert.Equal(
            "group 'items.batch' would bring this run to 9 step instances, and at most 5 are allowed (maxInstances). A step instance is one run of a step: one for each work item, each iteration and each visit. Raise maxInstances, or lower a loop's bounds",
            uncapped.Instances["items.batch"].Error);
    }

    /// <summary>
    /// The run's own recovery, taken for a failure inside a used workflow, reads the run's own inputs and params: its
    /// visit stands in the scope the failure happened in, and every lookup across a use's boundary is made in the scope
    /// that declares the recovery - the flow's root - never in the used workflow its source failed in, whose own input
    /// and param of other values it would otherwise read.
    /// </summary>
    [Fact]
    public async Task TheRunsRecovery_TakenForAStepOfAUsedWorkflow_ReadsTheRunsOwnInputsAndParams()
    {
        var failures = 0;
        var runner = new FakeRunner().On("verify", () => ++failures == 1
            ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed)
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var definition = Resolved("""
            name: flow
            inputs:
              - name: title
                kind: text
            params:
              - name: tone
                default: loud
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                onFailure: recover
                with:
                  title: the pipeline's own
                  prompt: prompts/implement.md
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: heal
                  type: orient
                  when: 'input.title == "the run''s own" && param.tone == "loud"'
            """, Pipeline[..Pipeline.IndexOf("\nrecover:", StringComparison.Ordinal)]);

        var coordinator = new FlowCoordinator(definition, runner, Facts(), new JumpingClock(), inputs: new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "the run's own" });
        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The recovery's step read the run's title and tone - the pipeline's are 'the pipeline's own' and 'plain' - and ran.
        Assert.Equal(["items.implement", "items.verify", "items.recover.heal", "items.verify+2"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items.recover.heal"].State);
    }

    /// <summary>
    /// A failure policy a group of a used workflow writes is filled in the scope that declares the group - from that
    /// workflow's own inputs - however deep the failing member is: a member of a workflow nested in it, with no policy
    /// of its own, reads the group's <c>{input.failTo}</c> as the used workflow filled it (<c>recover</c>, here, so the
    /// recovery takes the member's failure where it happened), never as the nested workflow fills an input of the same
    /// name.
    /// </summary>
    [Fact]
    public async Task APolicyAGroupOfAUsedWorkflowWrites_IsFilledFromThatWorkflowsInputs_HoweverDeepTheFailure()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["flow/outer/outer.yml"] = """
                name: outer
                inputs:
                  - name: failTo
                    kind: step
                    default: stop
                steps:
                  - name: listing
                    type: orient
                  - name: sub
                    use: ./inner/inner.yml
                    forEach: listing.list
                    dependsOn: [listing]
                    onFailure: '{input.failTo}'
                    with:
                      failTo: pause
                """,
            ["flow/outer/inner/inner.yml"] = """
                name: inner
                inputs:
                  - name: failTo
                    kind: step
                    default: stop
                steps:
                  - name: work
                    type: orient
                    retries: 0
                """,
        };
        var failures = 0;
        var runner = new FakeRunner()
            .On("listing", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["list"] = new JsonArray(1) }))
            .On("work", () => ++failures == 1 ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed) : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var definition = WorkflowResolver.Resolve(Parse("""
            name: flow
            steps:
              - name: items
                use: ./outer/outer.yml
                with:
                  failTo: recover
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: heal
                  type: orient
            """), "flow", path => files.GetValueOrDefault(path)).Definition;

        var coordinator = new FlowCoordinator(definition, runner, Facts(), new JumpingClock());
        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The recovery took the member's failure in the item it failed in, and the member ran again there.
        Assert.Equal(["items.listing", "items.sub[0].work", "items.sub[0].recover.heal", "items.sub[0].work+2"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items"].State);
    }

    /// <summary>
    /// A failure policy read from an input that fills with no policy is never dropped in silence nor routed by a guess:
    /// the step's error says which policy could not be read and why, and the failure goes where the top level sends
    /// what nothing else decides - the run stops, for it declares no recovery - rather than to the group around it as
    /// though the step wrote none.
    /// </summary>
    [Fact]
    public async Task AFailurePolicyThatCannotBeRead_IsSaid_AndGoesWhereTheTopLevelSendsIt()
    {
        var pipeline = Swap(Pipeline, "    retries: 1\n    onFailure: recover\n", "    retries: 0\n    onFailure: '{input.failTo}'\n");
        var runner = new FakeRunner().On("verify", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "verify failed"));
        var coordinator = new FlowCoordinator(Resolved("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
                  failTo: onward
            """, pipeline), runner, Facts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        var verify = coordinator.Instances["items.verify"];
        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal(
            "verify failed; and its failure policy could not be read: expression 'onFailure' of 'items.verify': '{input.failTo}' fills with 'onward', which is none of "
                + string.Join(", ", WorkflowStep.FailurePolicies.Select(policy => $"'{policy}'")),
            verify.Error);
        Assert.NotEqual(FlowFailure.TakenByGroup, verify.Failure);
    }

    /// <summary>
    /// A handler's <c>then</c> read from an input that reads nothing is never dropped in silence: the handler ends failed,
    /// saying which <c>then</c> could not be read and why, and its failure decides as any failure does - the redirect it
    /// could not make is never taken for one made.
    /// </summary>
    [Fact]
    public async Task AThenThatCannotBeRead_EndsItsHandlerFailed_SayingWhy()
    {
        var pipeline = Swap(Pipeline, "  - name: failTo\n    kind: step\n    default: continue\n", "  - name: failTo\n    kind: step\n    default: continue\n  - name: back\n    kind: step\n");
        pipeline = Swap(pipeline, "    retries: 1\n    onFailure: recover\n", "    retries: 0\n  - name: mend\n    type: orient\n    on:\n      step: verify\n      outcome: failure\n    then: '{input.back}'\n");
        var runner = new FakeRunner().On("verify", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "verify failed"));
        var coordinator = new FlowCoordinator(Resolved("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
            """, pipeline), runner, Facts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        var mend = coordinator.Instances["items.mend"];
        Assert.Equal((FlowInstanceState.Failed, (int?)HarnessExit.ConfigInvalid), (mend.State, mend.Exit));
        Assert.Equal("expression 'then' of 'items.mend': '{input.back}' reads nothing", mend.Error);
        Assert.Equal(["items.implement", "items.verify", "items.mend"], runner.Started);
    }

    /// <summary>
    /// A handler of a used workflow takes the step its <c>then</c> sends the run to from one of that workflow's step-kind
    /// inputs, <c>{input.back}</c> whole, filled by the use's with: the file reads, and the run goes back to the step the
    /// use names.
    /// </summary>
    [Fact]
    public async Task AThenReadFromAStepInput_SendsTheRunToTheStepTheUseNames()
    {
        var pipeline = Swap(Pipeline, "  - name: failTo\n    kind: step\n    default: continue\n", "  - name: failTo\n    kind: step\n    default: continue\n  - name: back\n    kind: step\n");
        pipeline = Swap(pipeline, "    retries: 1\n    onFailure: recover\n", "    retries: 0\n  - name: mend\n    type: orient\n    on:\n      step: verify\n      outcome: failure\n    then: '{input.back}'\n");
        var failures = 0;
        var runner = new FakeRunner().On("verify", () => ++failures == 1 ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed) : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var (definition, problems) = ResolvedWithProblems("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
                  back: implement
            """, pipeline);

        Assert.Empty(problems);

        var coordinator = new FlowCoordinator(definition, runner, Facts(), new JumpingClock());
        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["items.implement", "items.verify", "items.mend", "items.implement+2", "items.verify+2"], runner.Started);
    }

    /// <summary>
    /// A handler of a used workflow at its bound after a failure is not asked about where the used workflow's own
    /// recovery reaches the failure - though the run declares none: the failure is left to that recovery, which takes
    /// it, and no question of the handler's bound is raised.
    /// </summary>
    [Fact]
    public async Task AHandlerAtItsBoundInsideAUse_LeavesTheFailureToTheUsedWorkflowsRecovery()
    {
        var pipeline = Swap(Pipeline, "    retries: 1\n    onFailure: recover\n", "    retries: 0\n    onFailure: recover\n  - name: mend\n    type: orient\n    maxVisits: 1\n    on:\n      step: verify\n      outcome: failure\n    then: implement\n");
        var failures = 0;
        var runner = new FakeRunner().On("verify", () => ++failures <= 2 ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed) : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(Resolved("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
            """, pipeline), runner, Facts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Empty(coordinator.Questions);
        Assert.Equal(["items.implement", "items.verify", "items.mend", "items.implement+2", "items.verify+2", "items.recover.diagnose", "items.verify+3"], runner.Started);
    }

    /// <summary>
    /// A step of a used workflow whose with could not be read reads no input at all: an always step of it, which runs
    /// though its use failed, is refused naming the use whose inputs were never filled - never handed the calling run's
    /// input of the same name in their place.
    /// </summary>
    [Fact]
    public async Task AnInputReadInsideAUseWhoseWithCouldNotBeRead_IsRefused_NeverTheCallersInput()
    {
        var pipeline = Swap(Pipeline, "  - name: verify\n", "  - name: tidy\n    type: orient\n    always: true\n    when: 'input.title == \"the run''s own\"'\n  - name: verify\n");
        var coordinator = new FlowCoordinator(Resolved("""
            name: flow
            inputs:
              - name: title
                kind: text
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: '{nothing.here}'
                  prompt: prompts/implement.md
            """, pipeline), new FakeRunner(), Facts(), new JumpingClock(), inputs: new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "the run's own" });

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["items"].State);
        var tidy = coordinator.Instances["items.tidy"];
        Assert.Equal((FlowInstanceState.Failed, (int?)HarnessExit.ConfigInvalid), (tidy.State, tidy.Exit));
        Assert.Equal(
            "expression 'when' of 'items.tidy': the inputs of the workflow 'flow/pipeline' were never filled in 'items': its with could not be read, and another workflow's inputs are never read in their place",
            tidy.Error);
    }

    /// <summary>
    /// A forEach that uses a workflow reads every item's with values before it opens any item: one item's that cannot be
    /// read fails the group with none of its items opened - no instance, and no record a resume could not place - said
    /// with the input and the item it was read for.
    /// </summary>
    [Fact]
    public async Task AForEachWhoseItemsWithCannotBeRead_FailsWithNoneOfItsItemsOpened()
    {
        var runner = new FakeRunner().On("listing", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["list"] = new JsonArray(new JsonObject { ["label"] = "a" }, new JsonObject()) }));
        var coordinator = new FlowCoordinator(Resolved("""
            name: flow
            steps:
              - name: listing
                type: orient
              - name: items
                use: ./pipeline/pipeline.yml
                forEach: listing.list
                dependsOn: [listing]
                with:
                  title: '{item.label}'
                  prompt: prompts/implement.md
            """), runner, Facts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        var items = coordinator.Instances["items"];
        Assert.Equal((FlowInstanceState.Failed, (int?)HarnessExit.ConfigInvalid), (items.State, items.Exit));
        Assert.Equal("expression 'with.title' of 'items[1]': '{item.label}' reads nothing", items.Error);
        Assert.DoesNotContain(coordinator.Instances.Keys, id => id.StartsWith("items[", StringComparison.Ordinal));
        Assert.Equal(["listing"], runner.Started);
    }

    /// <summary>
    /// A used workflow's declared output read from a step its item skipped - which never produced it - is absent, and the
    /// use ends done: what a skipped step never wrote is nothing to read, not a failure.
    /// </summary>
    [Fact]
    public async Task AUsedWorkflowsOutput_FromAStepItSkipped_IsAbsent_AndTheUseEndsDone()
    {
        var pipeline = Swap(Pipeline, "    outputs: schemas/implement.json\n", "    outputs: schemas/implement.json\n    when: 'false'\n");
        var coordinator = new FlowCoordinator(Resolved("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
            """, pipeline), new FakeRunner(), Facts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["items.implement"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items"].State);
        Assert.False((coordinator.Instances["items"].Output as JsonObject)?.ContainsKey("changed") ?? false);
    }

    /// <summary>
    /// A used workflow's declared output that cannot be read fails the use, said with the output's name - never ends it
    /// done over an output it could not read.
    /// </summary>
    [Fact]
    public async Task AUsedWorkflowsOutput_ThatCannotBeRead_FailsTheUse()
    {
        var pipeline = Swap(Pipeline, "outputs:\n  - name: changed\n    from: implement.latest.changed\n", "outputs:\n  - name: changed\n    from: implement.latest.changed\n  - name: notes\n    from: text.notes\n");
        var coordinator = new FlowCoordinator(Resolved("""
            name: flow
            steps:
              - name: items
                use: ./pipeline/pipeline.yml
                with:
                  title: t
                  prompt: prompts/implement.md
            """, pipeline), new FakeRunner(), Facts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        var items = coordinator.Instances["items"];
        Assert.Equal((FlowInstanceState.Failed, (int?)HarnessExit.ConfigInvalid), (items.State, items.Exit));
        Assert.Equal("expression 'outputs.notes' of 'items': the workflow 'flow/pipeline' declares no text 'notes'", items.Error);
    }

    /// <summary>
    /// A repeat that uses a workflow fills its inputs for each iteration - its steps read the use's values, never the
    /// calling run's - and its declared outputs are read from its last iteration as the loop ends, the step's own for a
    /// later step to read.
    /// </summary>
    [Fact]
    public async Task ARepeatThatUsesAWorkflow_FillsItsInputsEachIteration_AndItsOutputsAsItEnds()
    {
        var pipeline = Swap(Pipeline, "    outputs: schemas/implement.json\n", "    outputs: schemas/implement.json\n    when: 'input.title == \"filled\"'\n");
        var runner = new FakeRunner().On("implement", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["changed"] = new JsonArray("src/a.cs") }));
        var coordinator = new FlowCoordinator(Resolved("""
            name: flow
            inputs:
              - name: title
                kind: text
            steps:
              - name: loop
                use: ./pipeline/pipeline.yml
                repeat:
                  until: 'iteration == 2'
                  maxIterations: 3
                with:
                  title: filled
                  prompt: prompts/implement.md
              - name: after
                type: orient
                dependsOn: [loop]
                when: 'loop.changed[0] == "src/a.cs"'
            """, pipeline), runner, Facts(), new JumpingClock(), inputs: new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "the run's own" });

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["loop#1.implement", "loop#1.verify", "loop#2.implement", "loop#2.verify", "after"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["loop"].State);
        Assert.Equal("""["src/a.cs"]""", (coordinator.Instances["loop"].Output as JsonObject)?["changed"]?.ToJsonString());
    }

    /// <summary>
    /// A handler that uses a workflow fills its inputs where it is raised, as any use does - its steps read the values
    /// its with gives, never the calling run's input of the same name.
    /// </summary>
    [Fact]
    public async Task AHandlerThatUsesAWorkflow_FillsItsInputs()
    {
        var pipeline = Swap(Pipeline, "    outputs: schemas/implement.json\n", "    outputs: schemas/implement.json\n    when: 'input.title == \"filled\"'\n");
        var runner = new FakeRunner().On("probe", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed));
        var coordinator = new FlowCoordinator(Resolved("""
            name: flow
            inputs:
              - name: title
                kind: text
            steps:
              - name: probe
                type: orient
                retries: 0
              - name: heal
                use: ./pipeline/pipeline.yml
                on:
                  step: probe
                  outcome: failure
                with:
                  title: filled
                  prompt: prompts/implement.md
            """, pipeline), runner, Facts(), new JumpingClock(), inputs: new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "the run's own" });

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["probe", "heal.implement", "heal.verify"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["heal.implement"].State);
    }

    /// <summary>
    /// Which use governs a step is read key by key, by the step's declaration path, through recoveries as through any
    /// group: the nearest use around the step that declares the key - one that declares nothing of it governs nothing of
    /// it, and the next one out does - a use in the run's own recovery governing its steps there, and a used workflow's
    /// own recovery governed by that workflow. The one owner every reader asks: the engine's gates, retries, handoffs,
    /// caps and advisor, and each step's runner.
    /// </summary>
    [Fact]
    public void TheUseThatGovernsAKey_IsTheNearestThatDeclaresIt_RecoveriesIncluded()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["flow/pipeline/pipeline.yml"] = Swap(Pipeline, "name: pipeline\n", "name: pipeline\nmaxRetries: 1\nhandoffSeconds: 30\n"),
            ["flow/outer/pipeline/pipeline.yml"] = Swap(Pipeline, "name: pipeline\n", "name: pipeline\nmaxRetries: 1\nhandoffSeconds: 30\n"),
            ["flow/outer/outer.yml"] = """
                name: outer
                maxRetries: 2
                steps:
                  - name: inner
                    use: ./pipeline/pipeline.yml
                    useParentLimits: false
                    with:
                      title: t
                      prompt: prompts/implement.md
                """,
        };
        var definition = WorkflowResolver.Resolve(Parse("""
            name: flow
            steps:
              - name: items
                use: ./outer/outer.yml
                useParentLimits: false
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: heal
                  use: ./pipeline/pipeline.yml
                  useParentLimits: false
                  with:
                    title: t
                    prompt: prompts/implement.md
            """), "flow", path => files.GetValueOrDefault(path)).Definition;

        string? Governing(string path, FlowUseKey key) => FlowUse.Governing(definition, path, key)?.Path;

        // In the run's own recovery, and in the used workflow's recovery under it.
        Assert.Equal("recover.heal", Governing("recover.heal.implement", FlowUseKey.MaxRetries));
        Assert.Equal("recover.heal", Governing("recover.heal.recover.diagnose", FlowUseKey.MaxRetries));
        Assert.Null(Governing("recover.heal.implement", FlowUseKey.MaxReasoningAgents));

        // Key by key through nested uses: the inner declares maxRetries and handoffSeconds, the outer maxRetries alone.
        Assert.Equal("items.inner", Governing("items.inner.implement", FlowUseKey.MaxRetries));
        Assert.Equal("items.inner", Governing("items.inner.implement", FlowUseKey.HandoffSeconds));
        Assert.Equal("items", Governing("items.other", FlowUseKey.MaxRetries));
        Assert.Null(Governing("items.inner.implement", FlowUseKey.MaxParallelProcesses));

        // The retry rule every reader shares follows it.
        var implement = FlowScope.At("recover.heal.implement", definition)!.Step;
        Assert.Equal(1, FlowUse.RetriesOf(definition, "recover.heal.implement", implement));
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

        /// <summary>Every run the coordinator asked for, whole, in the order it asked.</summary>
        public List<FlowStepRun> Ran { get; } = [];

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
            // The coordinator may ask two runners at once, each on a thread of its own: what they record is written one
            // at a time, and read once the run has ended.
            lock (Started)
            {
                Started.Add(run.Instance);

                if (run.Prompts is { } asked)
                {
                    Asked[run.Instance] = asked;
                }

                Ran.Add(run);
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

    /// <summary>
    /// An advisor runner that records each asking's advisor and workflow, and the call whole, and recommends the answer
    /// it was made with - or obtains nothing, where it was made with none.
    /// </summary>
    /// <param name="answer">The answer it recommends; null to obtain none.</param>
    private sealed class FakeAdvisor(string? answer = null) : IFlowAdvisorRunner
    {
        /// <summary>Each asking's advisor agent and the workflow its files are read in, in the order they were asked.</summary>
        public List<(string Agent, string Workflow)> Called { get; } = [];

        /// <summary>Each asking's call, whole, in the order they were asked.</summary>
        public List<FlowAdvisorCall> Calls { get; } = [];

        /// <inheritdoc/>
        public Task<FlowAdvice> AdviseAsync(FlowAdvisorCall call, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Called.Add((call.Advisor.Agent, call.Workflow));
                Calls.Add(call);
            }

            return Task.FromResult(answer is null
                ? new FlowAdvice(Answer: null, Why: null, Text: null, NotObtained: "the test sought none")
                : new FlowAdvice(Answer: answer, Why: "the test recommends it", Text: null));
        }
    }
}
