using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using NamedFiles = DssHarness.Core.Orchestration.Flow.WorkflowFiles;

namespace DssHarness.Tests;

/// <summary>
/// The implementation workflow this build ships: embedded whole under the workflow's own layout, read back and held to every
/// rule a repository's is, and written by init file by file, keeping every file there.
/// </summary>
public sealed class WorkflowTemplatesTests
{
    /// <summary>
    /// This build's own implementation workflow holds: parsed and validated against every rule a repository's is held to -
    /// a build whose own workflow does not hold is a build that says so, never one that writes a definition it refuses.
    /// </summary>
    [Fact]
    public void ThisBuildsOwnImplementationWorkflow_HoldsAgainstEveryRule()
    {
        var definition = WorkflowTemplates.ShippedDefinition();

        Assert.Equal(WorkflowTemplates.ShippedWorkflowName, definition.Name);
        Assert.Equal(80, Count(definition.Steps));
        Assert.Equal(46, Count(definition.Recover!.Steps));
        Assert.Equal(46, Count([.. definition.Steps, .. definition.Recover.Steps], "reason"));
        Assert.Equal(5, definition.Inputs.Count);
    }

    /// <summary>
    /// The shipped flow is held to its own files, not only parsed beside them: where its diagnosis schema holds no
    /// <c>choices</c>, the question that shows them to a person is refused, and where the diagnosis prompt reads the
    /// iteration before, that prompt is - each named as the definition writes it.
    /// </summary>
    [Fact]
    public void TheShippedFlow_IsTypedByItsOwnSchemas_AndItsPromptsAreRead()
    {
        var definition = WorkflowTemplates.ShippedDefinition();
        var diagnosis = JsonNode.Parse(WorkflowTemplates.TextOf("schemas/diagnosis.schema.json")!)!.AsObject();
        var required = diagnosis["required"]!.AsArray();
        var prompt = WorkflowTemplates.TextOf("prompts/diagnose.md")!;
        var added = prompt.Count(character => character == '\n') + 1; // The line the read is added on, below the last.

        diagnosis["properties"]!.AsObject().Remove("choices");
        required.RemoveAt(required.Select(name => name!.GetValue<string>()).ToList().IndexOf("choices"));

        var problems = WorkflowValidator.Validate(definition, hasCommitTemplate: false, file => file switch
        {
            "schemas/diagnosis.schema.json" => diagnosis.ToJsonString(),
            "prompts/diagnose.md" => prompt + "{previous.course}\n",
            _ => WorkflowTemplates.TextOf(file),
        });

        Assert.Equal(
            [
                $"step 'recover.diagnose' prompt 'prompts/diagnose.md' line {added} reads 'previous.course': a prompt is what the first iteration is asked with too, which has none before it; the iteration before is read in a resumePrompt",
                "'recover.direction.question' reads 'diagnose.choices', and the schema 'schemas/diagnosis.schema.json' of step 'recover.diagnose' has no property 'choices'",
            ],
            problems);
    }

    /// <summary>
    /// The embedded set is whole: every file the templates declare is in the assembly, and every file the assembly
    /// carries under the workflow's root is one the templates declare - no orphan the definition cannot name, no named
    /// file the build lacks.
    /// </summary>
    [Fact]
    public void TheEmbeddedSet_IsWholeAndNoMore()
    {
        var carried = DirectoryDeclarations();

        Assert.Equal(
            [.. WorkflowTemplates.Files.Order(StringComparer.Ordinal)],
            [.. carried.Order(StringComparer.Ordinal)]);

        return;

        static IReadOnlyList<string> DirectoryDeclarations()
        {
            // The names this assembly carries, read back as EnsureShipped writes them: the workflow's
            // own files below its name, with '/' whatever separator the building machine used.
            var prefix = typeof(WorkflowTemplates).Assembly.GetName().Name + ".WorkflowTemplates/";

            return
            [
                .. typeof(WorkflowTemplates).Assembly.GetManifestResourceNames()
                    .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(BelowTheWorkflowsName)
                    .Select(name => name.Replace('\\', '/')),
            ];

            static string BelowTheWorkflowsName(string resource)
            {
                var below = resource[(resource.LastIndexOf(".WorkflowTemplates/", StringComparison.Ordinal) + ".WorkflowTemplates/".Length)..];
                var cut = below.IndexOfAny(['/', '\\']);

                return cut < 0 ? below : below[(cut + 1)..];
            }
        }
    }

    /// <summary>
    /// The implementation workflow is written file by file: where none of it is there, every file, in order; where some is, only
    /// what is missing, and every file there is kept as it is - an edited one too - and counted.
    /// </summary>
    [Fact]
    public void TheImplementationWorkflow_IsWrittenFileByFile_KeepingEveryFileThere()
    {
        using var temp = new TempDirectory();
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var name = WorkflowTemplates.ShippedWorkflowName;
        var workflows = temp.Combine("tree", ".harness-config", "workflows");

        var written = WorkflowTemplates.EnsureShipped(workflows, fileSystem);

        Assert.Equal([.. WorkflowTemplates.Files.Select(file => $".harness-config/workflows/{name}/{file}")], written.Created);
        Assert.Equal(0, written.Kept);
        Assert.Equal(WorkflowTemplates.Files.Count, Directory.GetFiles(workflows, "*", SearchOption.AllDirectories).Length);

        File.Delete(Path.Combine(workflows, name, "recommendations.md"));
        File.WriteAllText(Path.Combine(workflows, name, "agents", "advisor.md"), "edited\n");

        var finished = WorkflowTemplates.EnsureShipped(workflows, fileSystem);

        Assert.Equal([$".harness-config/workflows/{name}/recommendations.md"], finished.Created);
        Assert.Equal(WorkflowTemplates.Files.Count - 1, finished.Kept);
        Assert.Equal(WorkflowTemplates.TextOf("recommendations.md"), File.ReadAllText(Path.Combine(workflows, name, "recommendations.md")));
        Assert.Equal("edited\n", File.ReadAllText(Path.Combine(workflows, name, "agents", "advisor.md")));
    }

    /// <summary>
    /// Where the repository has its own definition of the workflow, init writes back only the missing shipped files that
    /// definition names: a file the repository took out with its step stays out (part 05), and a definition that does
    /// not read writes nothing more, since only its check can say what it names.
    /// </summary>
    [Fact]
    public void ARepositorysOwnDefinition_GetsBackOnlyTheShippedFilesItNames()
    {
        using var temp = new TempDirectory();
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var name = WorkflowTemplates.ShippedWorkflowName;
        var workflows = temp.Combine("tree", ".harness-config", "workflows");
        var directory = Path.Combine(workflows, name);

        WorkflowTemplates.EnsureShipped(workflows, fileSystem);

        // The repository keeps one step of the shipped flow, and its files; every other file is gone.
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        File.WriteAllText(
            Path.Combine(directory, $"{name}.yml"),
            $"""
            name: {name}
            steps:
              - name: triage
                type: reason
                agent: triage
                prompt: prompts/triage.md
                outputs: schemas/triage.schema.json
            """);

        var written = WorkflowTemplates.EnsureShipped(workflows, fileSystem);

        Assert.Equal(
            [
                $".harness-config/workflows/{name}/agents/triage.md",
                $".harness-config/workflows/{name}/prompts/triage.md",
                $".harness-config/workflows/{name}/schemas/triage.schema.json",
            ],
            written.Created);
        Assert.Equal(1, written.Kept);

        // A definition that does not read says nothing of what it names: nothing is written in its place, and the refusal
        // says why, as configuration that does not hold.
        File.WriteAllText(Path.Combine(directory, $"{name}.yml"), "name: [unclosed\n");
        File.Delete(Path.Combine(directory, "prompts", "triage.md"));

        var refusal = Assert.Throws<HarnessException>(() => WorkflowTemplates.EnsureShipped(workflows, fileSystem));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.False(File.Exists(Path.Combine(directory, "prompts", "triage.md")));
    }

    /// <summary>
    /// A repository's own definition that could not be read, where a shipped file is missing, refuses (13) saying why:
    /// which files it names cannot be told, and none is written in its place.
    /// </summary>
    [Fact]
    public void ARepositorysOwnDefinitionThatCouldNotBeRead_IsRefused_AndNothingIsWritten()
    {
        using var temp = new TempDirectory();
        var real = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var name = WorkflowTemplates.ShippedWorkflowName;
        var workflows = temp.Combine("tree", ".harness-config", "workflows");
        var directory = Path.Combine(workflows, name);

        WorkflowTemplates.EnsureShipped(workflows, real);
        File.Delete(Path.Combine(directory, "prompts", "plan.md"));

        var refusal = Assert.Throws<HarnessException>(() => WorkflowTemplates.EnsureShipped(workflows, new UnreadDefinition(real, $"{name}.yml")));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Equal(
            $".harness-config/workflows/{name}/{name}.yml is the repository's own definition of the {name} workflow, and could not be read "
                + "(it is held), so which shipped files it names cannot be told, and none was written; run 'dssharness init' again once it reads.",
            refusal.Message);
        Assert.False(File.Exists(Path.Combine(directory, "prompts", "plan.md")));
    }

    /// <summary>
    /// Every reasoning step of the shipped flow but the triage answers with a schema that holds preExisting - required, a
    /// list of what it met that was there before the run - and the triage's own schema holds what it decides.
    /// </summary>
    [Fact]
    public void EveryShippedReasoningSchema_HoldsPreExisting_AndTheTriagesHoldsWhatItDecides()
    {
        var definition = WorkflowTemplates.ShippedDefinition();
        var schemas = NamedFiles.Steps(definition)
            .Where(entry => entry.Step.Type == "reason" && entry.Step.Raised is null)
            .Select(entry => entry.Step.Outputs!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(10, schemas.Count);

        foreach (var schema in schemas)
        {
            var root = JsonNode.Parse(WorkflowTemplates.TextOf(schema)!)!.AsObject();
            var reported = root["properties"]!["preExisting"]!;

            Assert.Contains("preExisting", root["required"]!.AsArray().Select(member => member!.GetValue<string>()));
            Assert.Equal("array", reported["type"]!.GetValue<string>());
            Assert.Equal(["title", "description", "where", "evidence", "blocks"], reported["items"]!["required"]!.AsArray().Select(member => member!.GetValue<string>()));
        }

        var triage = JsonNode.Parse(WorkflowTemplates.TextOf("schemas/pre-existing.schema.json")!)!.AsObject();

        Assert.Equal(
            ["course", "title", "description", "where", "proof", "blocks", "related", "critical", "why", "duplicates"],
            triage["required"]!.AsArray().Select(member => member!.GetValue<string>()));
        Assert.Equal(["fix", "list", "duplicate", "unproven"], triage["properties"]!["course"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()));
    }

    /// <summary>
    /// No shipped agent file names a model, a tier or an effort: which model a step runs on is the step's to say, in the
    /// workflow's yml, so an agent file a repository reuses in a workflow of its own brings no choice of model with it.
    /// </summary>
    [Fact]
    public void NoShippedAgentFile_NamesAModelATierOrAnEffort()
    {
        string[] chosen = ["model", "tier", "effort"];
        var agents = WorkflowTemplates.Files.Where(file => file.StartsWith("agents/", StringComparison.Ordinal)).ToList();

        Assert.Equal(18, agents.Count);

        foreach (var agent in agents)
        {
            var lines = WorkflowTemplates.TextOf(agent)!.Split('\n');
            var close = Array.IndexOf(lines, "---", 1);

            Assert.True(lines[0] == "---" && close > 0, $"{agent} opens with no front matter");

            var keys = lines[1..close].Select(line => line.Split(':')[0].Trim());

            Assert.Empty(keys.Intersect(chosen, StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// The method this build ships plans and reviews by (user, 2026-10-17): a plan's items are sized to one builder's
    /// cycle each, every item - a further round's and a recovery's too - and the plan itself carry a definition of
    /// done, and the review loop stands while a critical, high or medium finding does: three rounds, three more while
    /// one stands, and what is left then is carried to the end of the run, written down for the person who reads the
    /// pull request, rather than stopping the run. Held on the shipped files that say it, so a change of method is a
    /// change of those files and of this test together.
    /// </summary>
    [Fact]
    public void TheShippedMethod_PlansByDefinitionsOfDone_AndStandsWhileAMediumFindingDoes()
    {
        var schema = JsonDocument.Parse(Read("schemas/plan.schema.json")).RootElement;

        Assert.Contains(schema.GetProperty("required").EnumerateArray(), required => required.GetString() == "done");
        Assert.Contains("one builder's cycle", schema.GetProperty("properties").GetProperty("items").GetProperty("description").GetString(), StringComparison.Ordinal);

        var item = schema.GetProperty("properties").GetProperty("items").GetProperty("items");

        Assert.Contains(item.GetProperty("required").EnumerateArray(), required => required.GetString() == "done");
        Assert.Equal("string", item.GetProperty("properties").GetProperty("done").GetProperty("type").GetString());

        var piece = schema.GetProperty("properties").GetProperty("scope").GetProperty("items").GetProperty("properties").GetProperty("item");

        Assert.Contains(piece.GetProperty("required").EnumerateArray(), required => required.GetString() == "done");

        // A further round's items and a recovery's are work items as the plan's are: each carries its definition of done.
        foreach (var further in new[] { "schemas/self-audit.schema.json", "schemas/diagnosis.schema.json" })
        {
            var items = JsonDocument.Parse(Read(further)).RootElement.GetProperty("properties").GetProperty("items").GetProperty("items");

            Assert.Contains(items.GetProperty("required").EnumerateArray(), required => required.GetString() == "done");
        }

        var workflow = Read("implementation.yml");

        Assert.Contains("rounds: 3", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "blocking: \"any(review.findings, it.severity != 'low' && it.severity != 'nit')\"",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("it.production && it.severity", workflow, StringComparison.Ordinal);

        var fix = StepAt(WorkflowTemplates.ShippedDefinition().Steps, "rounds", "items", "fix");

        Assert.Equal(3, fix.Repeat!.BlockersLimitRounds);
        Assert.Equal("review.findings", fix.Repeat.Leftover);
        Assert.Equal(new FlowAnswer("accept", null, null), fix.Answers["loopBound"]);
    }

    /// <summary>
    /// The shipped flow never stops for no reason (user, 2026-10-17): a run of it ends done, or asks a person only what
    /// a person must settle. Every step says what its failure does, in the file: onFailure: recover on each step that
    /// builds, tests, folds, checks, commits, pushes or waits - the check steps of a block whose handler takes the
    /// block's failure first say it through the block - and the three reads told on and never judged say continue;
    /// a reasoning step is never recovered (user, 2026-10-17): its agent is tried again and on its fallback, quota or a
    /// failing provider pauses the run, and what still fails is asked (onFailure: pause). The recovery says, in the
    /// file too, that the step that gave it its failure runs again once it is done (onSuccess: failed-step), and that a
    /// failure of its own work is asked (onFailure: pause) - no step calls itself; none of its steps gives it a failure. What is left to a person is the list below: what blocks the request, the plan's
    /// approval, what a builder says only a person can give, and what a diagnosis says only a person can lift - each
    /// with the advisor's recommendation and the choices.
    /// </summary>
    [Fact]
    public void TheShippedFlow_GivesEveryFailureToItsRecovery_AndAsksAPersonOnlyWhereOneMust()
    {
        var definition = WorkflowTemplates.ShippedDefinition();
        var recover = Assert.IsType<FlowRecover>(definition.Recover);

        Assert.Equal(FlowRecover.FailedStep, recover.OnSuccess);
        Assert.Equal(FlowRecover.PauseOnFailure, recover.OnFailure);
        Assert.Equal(5, recover.MaxVisits);
        Assert.Equal("diagnose", recover.Steps[0].Resume);
        Assert.Equal(["diagnose", "direction", "redirect", "remedies", "publish"], [.. recover.Steps.Select(step => step.Name)]);

        var passedOver = new List<string>();
        var untried = new List<string>();
        var untriaged = new List<string>();
        var otherwise = new List<string>();
        var asking = new List<string>();

        Walk(definition.Steps, parent: null, inherited: null, recovering: false);
        Walk(recover.Steps, parent: "recover", inherited: null, recovering: true);

        // Only the reads that are told on, never judged, pass a failure over; every reasoning step asks, every other
        // step of the flow gives its failure to the recovery, and no step of the recovery does.
        Assert.Equal(["ci", "rounds.items.reconcile.catch-up", "rounds.items.fix.revise.rereconcile.recatch-up", "recover.remedies.reconcile.catch-up", "recover.remedies.fix.revise.rereconcile.recatch-up"], passedOver);
        Assert.Empty(otherwise);

        // A limit of a reasoning step is answered a counted number of times and then asked, never failed into a recovery.
        foreach (var limit in new[] { "turnLimit", "truncated", "contextLimit" })
        {
            Assert.Null(definition.Answers[limit].Then);
        }

        // Every agent the flow runs says on itself how it is tried again before it asks: each reasoning step, the
        // advisor and the reporter, three times on each model, waiting 30 seconds and twice as long each time.
        Assert.Empty(untried);
        Assert.Equal((3, 30), (definition.Advisor!.Retries, definition.Advisor.BackoffSeconds));
        Assert.Equal((3, 30), (definition.Reporter!.Retries, definition.Reporter.BackoffSeconds));
        Assert.Equal((3, 30), (definition.MaxRetries, definition.BackoffSeconds));

        Assert.Equal(["blocked", "planning.approval", "rounds.items.item-blocked", "recover.direction", "recover.remedies.item-blocked"], asking);

        // Every reasoning step - the recovery's among them - sends what it met that was there before the run to the one
        // triage step, which is raised by such an issue, never scheduled, and gives one it decides to fix to the recovery.
        Assert.Empty(untriaged);
        var triage = Assert.Single(definition.Steps, step => step.Raised is not null);
        Assert.Equal(
            ("triage-pre-existing-issue", "reason", WorkflowStep.RaisedByPreExisting, WorkflowStep.Recover, (string?)null),
            (triage.Name, triage.Type, triage.Raised, triage.OnAddress, triage.OnPreExisting));

        return;

        void Walk(IReadOnlyList<WorkflowStep> steps, string? parent, string? inherited, bool recovering)
        {
            foreach (var step in steps)
            {
                var path = parent is null ? step.Name : $"{parent}.{step.Name}";
                var effective = step.OnFailure ?? inherited;
                var expected = step.Type == "reason" ? "pause" : recovering ? null : WorkflowStep.Recover;

                if (step.OnFailure == "continue")
                {
                    passedOver.Add(path);
                }
                else if (!step.IsGroup && effective != expected)
                {
                    otherwise.Add($"{path}: {effective ?? "nothing"}");
                }

                if (step.Type == "reason" && (step.Retries, step.BackoffSeconds) != (3, 30))
                {
                    untried.Add(path);
                }

                if (step.Type == "reason" && step.Raised is null && step.OnPreExisting != "triage-pre-existing-issue")
                {
                    untriaged.Add(path);
                }

                if (step.Type == "pause" && !step.Answers.ContainsKey("pause"))
                {
                    asking.Add(path);
                }

                Walk(step.Steps, path, effective, recovering);
            }
        }
    }

    /// <summary>
    /// The help, the README and the architecture say how a pre-existing issue is reported, triaged, fixed at its root or
    /// listed, as the grammar has it.
    /// </summary>
    [Fact]
    public void TheHelpTheReadmeAndTheArchitecture_SayWhereAPreExistingIssueGoes()
    {
        var help = Prose.Words(DssHarness.Core.Help.HelpTopics.Render("workflows"));
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            $"That step says raised: {WorkflowStep.RaisedByPreExisting} - it is raised once per issue and never scheduled, as the recovery is not - and "
                + "onAddress: recover: it reproduces the issue on the run's base commit, gives the recovery one that is in a step's way, related to "
                + "the work or critical, to fix at its root",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            "lists the rest, proven, with a title and a description, in the run's `pre-existing-issues.json`, shown in the pull request's body and the handoff",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "The validator holds the wiring (V51): the triage is at the top level, waits for nothing, nothing waits for it and no handler sends the run to it",
            architecture,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The README and the architecture say how a failure reaches the recovery and where the run goes after it, as the
    /// grammar has it: a step says onFailure: recover, the recovery says onSuccess and onFailure, and a reasoning step is
    /// never recovered.
    /// </summary>
    [Fact]
    public void TheReadmeAndTheArchitecture_SayHowAFailureReachesTheRecovery_AndWhereTheRunGoesAfter()
    {
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains("A step gives a failure nothing handles to the workflow's `recover` by saying `onFailure: recover`, itself or through a group around it", readme, StringComparison.Ordinal);
        Assert.Contains($"`onSuccess` says - `{FlowRecover.FailedStep}`, the step that gave it its failure, which runs again from where it stopped", readme, StringComparison.Ordinal);
        Assert.Contains("A reasoning step is never recovered: a failed agent is tried again (`retries`, waiting `backoffSeconds`, twice as long each time)", readme, StringComparison.Ordinal);
        Assert.Contains($"`onSuccess` says - `{FlowRecover.FailedStep}`, the step that gave the recovery its failure, which runs again from where it stopped", architecture, StringComparison.Ordinal);
        Assert.Contains($"`onFailure` says once its steps' retries are spent: `{FlowRecover.PauseOnFailure}` or `{FlowRecover.StopOnFailure}` - never the recovery again", architecture, StringComparison.Ordinal);
        Assert.Contains("A reasoning step is never recovered, by its own word or a group's", architecture, StringComparison.Ordinal);
    }

    private static string Read(string below)
    {
        var assembly = typeof(WorkflowTemplates).Assembly;

        // The name's tail is spelt with the machine's separator; the match is made on the '/' form.
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.Replace('\\', '/').Contains(".WorkflowTemplates/", StringComparison.Ordinal)
                && resource.Replace('\\', '/').EndsWith("/" + below, StringComparison.Ordinal));

        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);

        return reader.ReadToEnd();
    }

    private static WorkflowStep StepAt(IReadOnlyList<WorkflowStep> steps, params string[] path)
    {
        var step = steps.Single(candidate => candidate.Name == path[0]);

        return path.Length == 1 ? step : StepAt(step.Steps, path[1..]);
    }

    private static int Count(IReadOnlyList<WorkflowStep> steps, string? type = null)
        => steps.Count(step => type is null || step.Type == type)
            + steps.Where(step => step.Steps.Count > 0).Sum(step => Count(step.Steps, type));

    /// <summary>The real file system, but one file by its name cannot be read, as one another process holds.</summary>
    private sealed class UnreadDefinition(IFileSystem inner, string name) : PassThroughFileSystem(inner)
    {
        public override string ReadAllText(string path)
            => Path.GetFileName(path) == name ? throw new IOException("it is held.") : base.ReadAllText(path);
    }
}
